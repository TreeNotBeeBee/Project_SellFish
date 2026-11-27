using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.UI;

namespace WindowPilot;

internal static class ScreenPicker
{
    private static int _picking;

    internal static async Task<WindowTarget> PickWindowAsync(CancellationToken token)
    {
        var click = await PickClickAsync(token);
        token.ThrowIfCancellationRequested();
        if (click.ProcessId == Environment.ProcessId)
            throw new InvalidOperationException("不能绑定 sell fish 自身的窗口，请重新点选其他应用。");
        var target = WindowService.GetSelectableTarget(click.Root);
        if (target == null || target.ProcessId != click.ProcessId || target.ThreadId != click.ThreadId)
            throw new InvalidOperationException("请选择其他应用的普通窗口。桌面、任务栏和工具窗口不能绑定。");
        return target;
    }

    internal static async Task<Color> PickColorAsync(CancellationToken token)
    {
        var click = await PickClickAsync(token);
        token.ThrowIfCancellationRequested();
        // 屏幕像素按桌面的合成结果读取。采样放在钩子返回之后，也不会触发被点击的控件。
        return ReadPixel(click.Point);
    }

    private static async Task<PickedClick> PickClickAsync(CancellationToken token)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("请从控制面板开始点选。");
        if (Interlocked.CompareExchange(ref _picking, 1, 0) != 0)
            throw new InvalidOperationException("已有点选正在进行。请先完成点选，或按 Esc 取消。");
        try { return await CaptureClickAsync(dispatcher, token); }
        finally { Interlocked.Exchange(ref _picking, 0); }
    }

    private static async Task<PickedClick> CaptureClickAsync(DispatcherQueue dispatcher, CancellationToken token)
    {
        var completion = new TaskCompletionSource<PickedClick>(TaskCreationOptions.RunContinuationsAsynchronously);
        PickedClick? pressed = null;
        bool escapeHeld = false, cancelling = false;
        HookProc mouse = (code, message, data) =>
        {
            if (code >= 0 && message == 0x0201 && !completion.Task.IsCompleted && !token.IsCancellationRequested) // WM_LBUTTONDOWN
            {
                // MSLLHOOKSTRUCT 的前两个整数是有符号的物理屏幕坐标（副屏可能为负）。
                var point = Marshal.PtrToStructure<EffectNative.Point>(data);
                var root = GetAncestor(WindowFromPoint(point), 2); // GA_ROOT 保留独立对话框，子控件归到窗口。
                var thread = GetWindowThreadProcessId(root, out var pid);
                pressed = new(point, root, thread, pid);
                return 1;
            }
            if (code >= 0 && message == 0x0202 && pressed is { } click) // WM_LBUTTONUP
            {
                pressed = null;
                if (!cancelling && !token.IsCancellationRequested) completion.TrySetResult(click);
                else if (!escapeHeld) completion.TrySetCanceled();
                return 1;
            }
            return CallNextHookEx(0, code, message, data);
        };

        HookProc keyboard = (code, message, data) =>
        {
            if (code >= 0 && Marshal.ReadInt32(data) == 0x1B && !completion.Task.IsCompleted) // VK_ESCAPE
            {
                if (message is 0x0100 or 0x0104) // KEYDOWN / SYSKEYDOWN
                {
                    escapeHeld = cancelling = true;
                    return 1;
                }
                if (escapeHeld && message is (0x0101 or 0x0105)) // KEYUP / SYSKEYUP
                {
                    escapeHeld = false;
                    if (pressed == null) completion.TrySetCanceled();
                    return 1;
                }
            }
            return CallNextHookEx(0, code, message, data);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var timeout = deadline.Token.Register(() => completion.TrySetCanceled(deadline.Token));
        using var cancellation = token.Register(() =>
        {
            // 键鼠回调和取消操作在同一消息循环内处理。已拦过按下时，仍拦住对应的松开。
            if (!dispatcher.TryEnqueue(() =>
            {
                cancelling = true;
                if (pressed == null && !escapeHeld) completion.TrySetCanceled(token);
            })) completion.TrySetCanceled(token);
        });
        nint mouseHook = 0, keyboardHook = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            var module = EffectNative.GetModuleHandle(null);
            mouseHook = SetWindowsHookExW(14, mouse, module, 0); // WH_MOUSE_LL
            if (mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动鼠标点选。");
            keyboardHook = SetWindowsHookExW(13, keyboard, module, 0); // WH_KEYBOARD_LL
            if (keyboardHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用 Esc 取消点选。");
            return await completion.Task;
        }
        finally
        {
            if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            GC.KeepAlive(keyboard);
            GC.KeepAlive(mouse);
        }
    }

    private static Color ReadPixel(EffectNative.Point point)
    {
        nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
        try
        {
            if (screen == 0 || (memory = EffectNative.CreateCompatibleDC(screen)) == 0 ||
                (bitmap = CreateCompatibleBitmap(screen, 1, 1)) == 0 ||
                (previous = EffectNative.SelectObject(memory, bitmap)) == 0)
                throw new InvalidOperationException("无法创建屏幕取色表面。");

            const uint copyLayeredPixel = 0x40CC0020; // SRCCOPY | CAPTUREBLT，包含可见的分层窗口。
            if (!BitBlt(memory, 0, 0, 1, 1, screen, point.X, point.Y, copyLayeredPixel))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法复制所选的屏幕像素。");
            uint rgb = GetPixel(memory, 0, 0);
            if (rgb == uint.MaxValue) throw new InvalidOperationException("无法读取所选的屏幕颜色。");
            return Color.FromArgb(255, (byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
        }
        finally
        {
            if (previous != 0) EffectNative.SelectObject(memory, previous);
            if (bitmap != 0) EffectNative.DeleteObject(bitmap);
            if (memory != 0) EffectNative.DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    private readonly record struct PickedClick(EffectNative.Point Point, nint Root, uint ThreadId, uint ProcessId);
    private delegate nint HookProc(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint msg, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(EffectNative.Point point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dst, int x, int y, int width, int height, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}
