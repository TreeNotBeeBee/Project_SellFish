using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Microsoft.UI.Dispatching;

namespace WindowPilot;

internal static class NativeControlPicker
{
    internal static async Task<NativeControlBinding> PickAsync(WindowTarget target, CancellationToken token)
    {
        var queue = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("请从控制面板选择控件。");
        var point = new TaskCompletionSource<PickPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        PickPoint? pressed = null;
        nint hook = 0;
        MouseProc capture = (code, message, data) =>
        {
            if (code >= 0 && message == 0x0201) // WM_LBUTTONDOWN
            {
                var cursor = Marshal.PtrToStructure<PickPoint>(data);
                if (GetAncestor(WindowFromPoint(cursor), 2) == target.Handle)
                {
                    pressed = cursor;
                    return 1;
                }
                point.TrySetException(new InvalidOperationException("请在当前绑定的窗口中选择控件。"));
            }
            if (code >= 0 && message == 0x0202 && pressed.HasValue)
            {
                point.TrySetResult(pressed.Value);
                return 1;
            }
            return CallNextHookEx(0, code, message, data);
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var registration = timeout.Token.Register(() => point.TrySetCanceled(timeout.Token));
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(30);
        timer.Tick += (_, _) =>
        {
            if (GetAsyncKeyState(0x1B) < 0 || !IsWindow(target.Handle)) point.TrySetCanceled();
        };
        PickPoint picked;
        try
        {
            token.ThrowIfCancellationRequested();
            hook = SetWindowsHookExW(14, capture, GetModuleHandleW(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动控件点选。");
            timer.Start();
            picked = await point.Task;
        }
        finally
        {
            timer.Stop();
            if (hook != 0) UnhookWindowsHookEx(hook);
            GC.KeepAlive(capture);
        }
        return await LocateAsync(target, picked.X, picked.Y, token);
    }

    internal static Task<NativeControlBinding> LocateAsync(WindowTarget target, int x, int y, CancellationToken token) =>
        UiaThread.Run(api =>
        {
            token.ThrowIfCancellationRequested();
            if (GetAncestor(WindowFromPoint(new(x, y)), 2) != target.Handle) throw InvalidTarget();
            VerifyWindow(target);
            var leaf = api.ElementFromPoint(new tagPOINT { x = x, y = y });
            var path = GetSafePath(api, leaf, target);
            IUIAutomationElement? candidate = null;
            foreach (var item in path)
            {
                int type = item.CurrentControlType;
                if (type == UIA_ControlTypeIds.UIA_EditControlTypeId) { candidate = item; break; }
                if (type == UIA_ControlTypeIds.UIA_TextControlTypeId || type == UIA_ControlTypeIds.UIA_DocumentControlTypeId)
                    candidate ??= item;
                else if (type != UIA_ControlTypeIds.UIA_CustomControlTypeId && type != UIA_ControlTypeIds.UIA_PaneControlTypeId
                    && type != UIA_ControlTypeIds.UIA_GroupControlTypeId)
                    break;
            }
            if (candidate == null) throw new InvalidOperationException("这个控件没有公开受支持的文本接口。");
            var binding = new NativeControlBinding(target, candidate);
            binding.Initialize(api);
            token.ThrowIfCancellationRequested();
            return binding;
        });

    internal static List<IUIAutomationElement> GetSafePath(IUIAutomation api, IUIAutomationElement leaf, WindowTarget target)
    {
        VerifyWindow(target);
        var root = api.ElementFromHandle(target.Handle);
        var walker = api.RawViewWalker;
        var path = new List<IUIAutomationElement>();
        for (var item = leaf; item != null && path.Count < 64; item = walker.GetParentElement(item))
        {
            if (item.CurrentIsPassword != 0) throw new InvalidOperationException("密码控件不能分离。");
            path.Add(item);
            if (api.CompareElements(item, root) != 0) return path;
        }
        throw InvalidTarget();
    }

    internal static void VerifyWindow(WindowTarget target)
    {
        uint thread = GetWindowThreadProcessId(target.Handle, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId || pid != target.ProcessId || thread != target.ThreadId)
            throw InvalidTarget();
        if (target.ProcessStartTicks != 0)
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.StartTime.ToUniversalTime().Ticks != target.ProcessStartTicks) throw InvalidTarget();
        }
    }

    internal static InvalidOperationException InvalidTarget() => new("控件已消失或不属于原绑定窗口，请重新选择。");

    [StructLayout(LayoutKind.Sequential)] private readonly record struct PickPoint(int X, int Y);
    private delegate nint MouseProc(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, MouseProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint msg, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PickPoint point);
}

/// <summary>UIA foreign-provider calls live on a persistent MTA, never on the WinUI dispatcher.</summary>
internal static class UiaThread
{
    private static readonly BlockingCollection<Action> Jobs = new();
    private static IUIAutomation? _api;

    static UiaThread()
    {
        var thread = new Thread(() => { foreach (var job in Jobs.GetConsumingEnumerable()) job(); })
            { IsBackground = true, Name = "sell fish accessibility" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal static Task<T> Run<T>(Func<IUIAutomation, T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Jobs.Add(() =>
        {
            try
            {
                if (_api == null)
                {
                    var api = (IUIAutomation2)new CUIAutomation8();
                    api.ConnectionTimeout = 1500;
                    api.TransactionTimeout = 1500;
                    _api = (IUIAutomation)api;
                }
                completion.TrySetResult(action(_api));
            }
            catch (COMException ex) { completion.TrySetException(new InvalidOperationException("控件未响应或已失效，请在原窗口重新选择。", ex)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }
}

internal sealed class NativeControlBinding(WindowTarget target, IUIAutomationElement element) : IDisposable
{
    private const int MaxTextLength = 256 * 1024;
    private volatile bool _closed;
    private int[] _id = [];
    internal string Key { get; private set; } = "";
    internal string Label { get; private set; } = "";
    internal string InitialText { get; private set; } = "";
    internal bool Writable { get; private set; }

    internal void Initialize(IUIAutomation api)
    {
        _id = element.GetRuntimeId();
        if (_id is not { Length: > 0 }) throw NativeControlPicker.InvalidTarget();
        Key = target.Handle.ToString("X") + ":" + string.Join(".", _id);
        var value = ValuePattern();
        Writable = value != null && value.CurrentIsReadOnly == 0 && element.CurrentIsEnabled != 0 &&
            element.CurrentControlType == UIA_ControlTypeIds.UIA_EditControlTypeId;
        InitialText = Read(api);
        Label = LabelFor(api);
    }

    private string LabelFor(IUIAutomation api)
    {
        bool edit = element.CurrentControlType == UIA_ControlTypeIds.UIA_EditControlTypeId;
        string name = "";
        try
        {
            if (element.CurrentLabeledBy is { } label)
            {
                NativeControlPicker.GetSafePath(api, label, target);
                name = label.CurrentName ?? "";
            }
            if (string.IsNullOrWhiteSpace(name) && edit) name = element.CurrentName ?? "";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* A label is optional. */ }
        string kind = edit ? "输入框" : "文本";
        name = name.Trim();
        if (name.Length == 0 || name == InitialText.Trim() || name.Contains('\n') || name.Contains('\r')) return kind;
        return kind + " · " + (name.Length <= 24 ? name : name[..24] + "…");
    }

    private IUIAutomationValuePattern? ValuePattern() =>
        element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) as IUIAutomationValuePattern;

    private string Read(IUIAutomation api)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        NativeControlPicker.GetSafePath(api, element, target);
        if (!_id.SequenceEqual(element.GetRuntimeId())) throw NativeControlPicker.InvalidTarget();
        var value = ValuePattern();
        string text;
        if (value != null) text = value.CurrentValue ?? "";
        else if (element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern document)
            text = document.DocumentRange.GetText(MaxTextLength + 1) ?? "";
        else if (element.CurrentControlType == UIA_ControlTypeIds.UIA_TextControlTypeId) text = element.CurrentName ?? "";
        else throw new InvalidOperationException("这个控件没有公开可读取的文本。");
        if (text.Length > MaxTextLength) throw new InvalidOperationException("控件文字过长，请选择较小的文本区域。");
        return text;
    }

    internal Task<string> ReadAsync() => UiaThread.Run(Read);
    internal Task<string> WriteAsync(string expected, string replacement) => UiaThread.Run(api =>
    {
        if (replacement.Length > MaxTextLength) throw new InvalidOperationException("输入内容过长。");
        if (Read(api) != expected) throw new InvalidOperationException("原控件已更改。请先重新载入原文，再编辑写回。");
        var value = ValuePattern();
        if (!Writable || value == null || value.CurrentIsReadOnly != 0 || element.CurrentIsEnabled == 0)
            throw new InvalidOperationException("该控件当前不能写入。");
        ObjectDisposedException.ThrowIf(_closed, this);
        value.SetValue(replacement);
        string accepted = Read(api);
        if (accepted != replacement) throw new InvalidOperationException("原应用未接受完整文字。可重新载入实际内容。");
        return accepted;
    });

    public void Dispose() => _closed = true;
}
