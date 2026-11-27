using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WindowPilot;

/// <summary>让资源管理器在通知区域显示鱼篓，并把鼠标或键盘激活转给主面板。</summary>
internal sealed class NotificationAreaIcon : IDisposable
{
    private const uint IconId = 1;
    private const nuint SubclassId = 0x5346;
    private const uint Add = 0, Modify = 1, Delete = 2, SetVersion = 4;
    private const uint Select = 0x400, KeySelect = 0x401, LeftButtonUp = 0x202;
    private static readonly uint CallbackMessage = RegisterWindowMessageW("WindowPilot.NotificationArea.Callback");
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    internal static readonly uint ShowController = RegisterWindowMessageW("WindowPilot.ShowController");

    private readonly nint _hwnd;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _retry;
    private readonly Action _reveal;
    private readonly SubclassProc _proc;
    private IconData _data;
    private bool _disposed;

    internal NotificationAreaIcon(Window window, string iconPath, Action reveal)
    {
        if (CallbackMessage == 0 || TaskbarCreated == 0 || ShowController == 0)
            throw new InvalidOperationException("无法注册通知区域窗口消息。");
        _hwnd = WindowNative.GetWindowHandle(window);
        _queue = window.DispatcherQueue;
        _reveal = reveal;
        _proc = WindowProc;
        _retry = _queue.CreateTimer();
        _retry.Interval = TimeSpan.FromSeconds(4);
        _retry.Tick += OnRetry;
        // 便携版可从不同目录启动。用 HWND + ID 标识图标，避免 GUID 绑定到旧版 exe 的路径。
        _data = new IconData
        {
            Size = (uint)Marshal.SizeOf<IconData>(), Window = _hwnd, Id = IconId,
            Flags = 0x01 | 0x02 | 0x04 | 0x80, // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
            Callback = CallbackMessage, Version = 4, Tip = "sell fish · 单击找回主面板",
            Icon = LoadImageW(0, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49 /* SM_CXSMICON */),
                GetSystemMetrics(50 /* SM_CYSMICON */), 0x10 /* LR_LOADFROMFILE */)
        };
        if (_data.Icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowSubclass(_hwnd, _proc, SubclassId, 0))
        {
            DestroyIcon(_data.Icon);
            throw new InvalidOperationException("无法接收通知区域图标消息。");
        }
        // 即使主面板以管理员身份运行，也能收到普通权限的资源管理器和二次启动消息。
        foreach (uint message in new[] { CallbackMessage, TaskbarCreated, ShowController })
            ChangeWindowMessageFilterEx(_hwnd, message, 1 /* MSGFLT_ALLOW */, 0);
        RegisterIcon();
    }

    private void RegisterIcon()
    {
        if (_disposed) return;
        // 在启动或重启 Explorer 时，收到广播和通知区域真正就绪之间可能有间隔。
        if (Shell_NotifyIconW(Add, ref _data) || Shell_NotifyIconW(Modify, ref _data))
        {
            Shell_NotifyIconW(SetVersion, ref _data);
            _retry.Stop();
        }
        else if (!_retry.IsRunning) _retry.Start();
    }

    private void OnRetry(DispatcherQueueTimer sender, object args) => RegisterIcon();

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == TaskbarCreated)
            _queue.TryEnqueue(RegisterIcon);
        else if (message == ShowController || message == CallbackMessage)
        {
            uint payload = unchecked((uint)lParam);
            bool selected = (payload >> 16) == IconId && (payload & 0xffff) is Select or KeySelect;
            // 如果 Shell 未接受版本 4，使用旧格式的按钮松开通知。
            bool legacyClick = wParam == IconId && payload == LeftButtonUp;
            if (message == ShowController || selected || legacyClick)
            {
                // 在响应用户的托盘点击时请求前台，再让 WinUI 在消息返回后展开和排版。
                SetForegroundWindow(hwnd);
                _queue.TryEnqueue(() => { if (!_disposed) _reveal(); });
            }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry.Stop();
        _retry.Tick -= OnRetry;
        Shell_NotifyIconW(Delete, ref _data);
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        DestroyIcon(_data.Icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern bool Shell_NotifyIconW(uint command, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeInfo);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint LoadImageW(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
