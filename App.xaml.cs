using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace WindowPilot;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private Window? _window;

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
            MessageBoxW(0, "窗口轻控已经在运行，请从任务栏打开。", "窗口轻控 · WindowPilot", 0x40);
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        };
        _window.Activate();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
