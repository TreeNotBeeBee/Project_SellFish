using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WindowPilot;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private Window? _window;
    private static readonly string InstanceProperty = AppProfile.MutexName + ".Controller";

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instanceMutex = new Mutex(false, AppProfile.MutexName);
        bool acquired;
        try { acquired = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            if (!ActivateExistingController())
                MessageBoxW(0, "已有窗口轻控实例正在运行。升级时，请先退出旧版，再打开 sell fish。", "sell fish", 0x40);
            Exit();
            return;
        }

        _window = new MainWindow();
        nint hwnd = WindowNative.GetWindowHandle(_window);
        SetPropW(hwnd, InstanceProperty, 1);
        _window.Closed += (_, _) =>
        {
            RemovePropW(hwnd, InstanceProperty);
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        };
        _window.Activate();
    }

    private static bool ActivateExistingController()
    {
        // Allow the first instance to finish creating its HWND on simultaneous launches.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            nint hwnd = 0;
            while ((hwnd = FindWindowExW(0, hwnd, "WinUIDesktopWin32WindowClass", "sell fish")) != 0)
            {
                if (GetPropW(hwnd, InstanceProperty) == 0) continue;
                // 新版会展开面板并在当前显示器找回；旧版仍可通过原来的窗口激活路径响应。
                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9); // SW_RESTORE
                SetForegroundWindow(hwnd);
                if (NotificationAreaIcon.ShowController != 0)
                    PostMessageW(hwnd, NotificationAreaIcon.ShowController, 0, 0);
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SetPropW(nint hwnd, string key, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetPropW(nint hwnd, string key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint RemovePropW(nint hwnd, string key);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
