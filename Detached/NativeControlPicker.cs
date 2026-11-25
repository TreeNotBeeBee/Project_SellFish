using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Microsoft.UI.Dispatching;
using static Interop.UIAutomationClient.UIA_ControlTypeIds;

namespace WindowPilot;

internal interface INativeTextSource : IDisposable
{
    string Key { get; }
    string Label { get; }
}

internal static class NativeControlPicker
{
    private static int _picking;

    internal static async Task<INativeTextSource> PickAsync(WindowTarget target, CancellationToken token, bool outputRegion)
    {
        var point = await PickPointAsync(target, token);
        return await LocateAsync(target, point.X, point.Y, token, outputRegion);
    }

    internal static Task<INativeTextSource> LocateAsync(WindowTarget target, int x, int y, CancellationToken token, bool outputRegion = false) =>
        UiaThread.ReadRegion(api =>
        {
            token.ThrowIfCancellationRequested();
            var source = LocateElement(api, target, ElementAt(api, target, x, y), x, y, outputRegion);
            if (token.IsCancellationRequested) { source.Dispose(); token.ThrowIfCancellationRequested(); }
            return source;
        });

    internal static async Task<(int X, int Y)> PickPointAsync(WindowTarget target, CancellationToken token)
    {
        var queue = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("请从控制面板选择控件。");
        // 面板和多个输入浮窗可同时请求点选，同一对鼠标按下/松开只属于一个请求。
        if (Interlocked.CompareExchange(ref _picking, 1, 0) != 0)
            throw new InvalidOperationException("已有点选正在进行。请先完成点选，或按 Esc 取消。");
        try { return await CapturePointAsync(queue, target, token); }
        finally { Interlocked.Exchange(ref _picking, 0); }
    }

    private static async Task<(int X, int Y)> CapturePointAsync(DispatcherQueue queue, WindowTarget target, CancellationToken token)
    {
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
        return (picked.X, picked.Y);
    }

    internal static IUIAutomationElement ElementAt(IUIAutomation api, WindowTarget target, int x, int y)
    {
        if (GetAncestor(WindowFromPoint(new(x, y)), 2) != target.Handle) throw InvalidTarget();
        VerifyWindow(target);
        var point = new tagPOINT { x = x, y = y };
        var leaf = api.ElementFromPoint(point);
        if (target.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) || leaf.CurrentFrameworkId == "Chrome")
        {
            // Chromium 异步通知渲染进程进行命中测试，第一次调用可能返回上一次点选的节点。
            // 即使它是有效控件也要再取一次，防止把另一个编辑框或按钮绑到本次点击。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(55);
                if (GetAncestor(WindowFromPoint(new(x, y)), 2) != target.Handle) throw InvalidTarget();
                leaf = api.ElementFromPoint(point);
                if (Contains(leaf.CurrentBoundingRectangle, x, y)) break;
            }
        }
        return leaf;
    }

    internal static INativeTextSource LocateElement(IUIAutomation api, WindowTarget target, IUIAutomationElement leaf,
        int x, int y, bool outputRegion = false)
    {
        var path = GetHitPath(api, target, leaf, x, y);
        var document = path.FirstOrDefault(e => e.CurrentControlType == UIA_DocumentControlTypeId);
        IUIAutomationElement? candidate = null;
        foreach (var item in path)
        {
            int type = item.CurrentControlType;
            if (type == UIA_EditControlTypeId) { candidate = item; break; }
            if (type is UIA_TextControlTypeId or UIA_DocumentControlTypeId)
            {
                candidate ??= item;
                if (type == UIA_DocumentControlTypeId) break;
            }
            else if (type is not (UIA_CustomControlTypeId or UIA_PaneControlTypeId or UIA_GroupControlTypeId))
                break;
        }
        if (outputRegion || (document != null && candidate?.CurrentControlType != UIA_EditControlTypeId && IsWebDocument(document)))
            return NativeTextRegion.Create(api, target, path);
        if (candidate == null) throw new InvalidOperationException("这个控件没有公开受支持的文本接口。");
        var binding = new NativeControlBinding(target, candidate);
        binding.Initialize(api);
        return binding;
    }

    private static bool IsWebDocument(IUIAutomationElement document) =>
        document.CurrentFrameworkId is "Chrome" or "Gecko" || DocumentAddress(document) != null;

    internal static List<IUIAutomationElement> GetHitPath(IUIAutomation api, WindowTarget target,
        IUIAutomationElement leaf, int x, int y)
    {
        var path = GetSafePath(api, leaf, target);
        bool inBounds = Contains(leaf.CurrentBoundingRectangle, x, y);
        if (inBounds && leaf.CurrentControlType != UIA_DocumentControlTypeId) return path;
        var document = path.FirstOrDefault(e => e.CurrentControlType == UIA_DocumentControlTypeId);
        if (document != null && IsWebDocument(document))
        {
            // Document 的 Value 是网址。按坐标沿原文档向下找，既能取正文，也能保留实际编辑框和按钮。
            if (!Contains(document.CurrentBoundingRectangle, x, y)) throw InvalidTarget();
            return GetSafePath(api, DescendantAtPoint(api, document, x, y), target);
        }
        if (!inBounds && (target.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) || leaf.CurrentFrameworkId == "Chrome"))
            throw new InvalidOperationException("浏览器正在更新控件位置，请重新点选。");
        return path;
    }

    private static bool Contains(tagRECT rect, int x, int y) =>
        x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom;

    private static IUIAutomationElement DescendantAtPoint(IUIAutomation api, IUIAutomationElement document, int x, int y)
    {
        var raw = api.RawViewCondition;
        var cache = api.CreateCacheRequest();
        cache.TreeFilter = raw;
        cache.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_IsOffscreenPropertyId);
        var current = document;
        // 原始树包含段落和静态文字。只枚举落点所在的分支；同层容器重叠时取覆盖落点的最小矩形。
        for (int depth = 0; depth < 64; depth++)
        {
            var children = current.FindAllBuildCache(TreeScope.TreeScope_Children, raw, cache);
            IUIAutomationElement? hit = null;
            double smallest = double.MaxValue;
            for (int i = 0, count = children.Length; i < count; i++)
            {
                var child = children.GetElement(i);
                var rect = child.CachedBoundingRectangle;
                if (child.CachedIsOffscreen != 0 || !Contains(rect, x, y)) continue;
                double area = ((double)rect.right - rect.left) * ((double)rect.bottom - rect.top);
                if (area <= smallest) { smallest = area; hit = child; }
            }
            if (hit == null) break;
            current = hit;
        }
        return current;
    }

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

    // 浏览器可能在同一个 UIA Document 内切换会话，因此在支持的平台上也保存该文档的完整地址。
    internal static string? DocumentAddress(IUIAutomationElement? document)
    {
        if (document == null) return null;
        var address = document.GetCurrentPropertyValue(UIA_PropertyIds.UIA_LegacyIAccessibleValuePropertyId) as string;
        if (string.IsNullOrEmpty(address) &&
            document.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern value)
            address = value.CurrentValue;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" ? address : null;
    }

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

/// <summary>UIA 调用在固定的 MTA 线程中串行执行。长文档的枚举使用独立队列，避免阻塞输入框的写回和发送。</summary>
internal static class UiaThread
{
    private static readonly Lazy<Worker> Controls = new(() => new("sell fish accessibility"));
    private static readonly Lazy<Worker> Regions = new(() => new("sell fish output regions"));

    internal static Task<T> Run<T>(Func<IUIAutomation, T> action) => Controls.Value.Run(action);
    internal static Task<T> ReadRegion<T>(Func<IUIAutomation, T> action) => Regions.Value.Run(action);

    private sealed class Worker
    {
        private readonly BlockingCollection<Action> _jobs = new();
        private IUIAutomation? _api;

        internal Worker(string name)
        {
            var thread = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) job(); })
                { IsBackground = true, Name = name };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        internal Task<T> Run<T>(Func<IUIAutomation, T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _jobs.Add(() =>
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
}
