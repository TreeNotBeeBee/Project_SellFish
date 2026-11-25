using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace WindowPilot;

// Both windows read and write controls owned by a separate process.
public partial class App : Application
{
    public App() => InitializeComponent();
    private readonly List<string> _log = [];
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 3 && arguments[1] == "--fixture")
        {
            MakeFixture(arguments[2]);
            return;
        }
        string report = Environment.GetEnvironmentVariable("SELL_FISH_DETACHED_RESULT") ??
            Path.Combine(Path.GetTempPath(), "sell-fish-detached-probe.txt");
        string name = "sell fish fixture " + Guid.NewGuid().ToString("N");
        using var fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
            ArgumentList = { "--fixture", name } })!;
        var windows = new List<Window>();
        try
        {
            // The application's dispatcher must remain alive while individual detached windows close.
            var anchor = new Window { Title = "sell fish probe", Content = new Grid() };
            windows.Add(anchor);
            anchor.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(600, 150, 220, 140));
            anchor.Activate();
            nint hwnd = 0;
            for (int i = 0; i < 80 && hwnd == 0; i++) { await Task.Delay(100); hwnd = FindWindowW("Static", name); }
            Check(hwnd != 0, "external target created");
            var target = new WindowTarget(hwnd, (uint)fixture.Id, GetWindowThreadProcessId(hwnd, out _), "Static", name, "DetachedProbe",
                fixture.StartTime.ToUniversalTime().Ticks);
            async Task<NativeControlBinding> Pick(int id)
            {
                nint child = GetDlgItem(hwnd, id);
                if (!GetWindowRect(child, out var r)) throw new Exception("Fixture control not created");
                return await NativeControlPicker.LocateAsync(target, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, default);
            }
            using var writable = await Pick(1);
            Check(writable.Writable && writable.InitialText == "initial", "Win32 Edit discovered");
            Check(await writable.WriteAsync("initial", "输入中文") == "输入中文", "write and read back Unicode text");
            try { await writable.WriteAsync("initial", "wrong"); throw new Exception("stale write succeeded"); }
            catch (InvalidOperationException) { Check(await writable.ReadAsync() == "输入中文", "stale writes preserve target"); }
            using var readOnly = await Pick(2);
            Check(!readOnly.Writable && readOnly.InitialText == "reference", "read-only native control");
            try { await Pick(3); throw new Exception("password accepted"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("密码"), "password excluded"); }
            try { await Pick(4); throw new Exception("button accepted"); }
            catch (InvalidOperationException) { Check(true, "unsupported native button excluded"); }

            var editor = new NativeTextWindow(await Pick(1));
            var reference = new NativeTextWindow(await Pick(2));
            nint foreground = GetForegroundWindow();
            foreach (var view in new[] { editor, reference })
            {
                windows.Add(view);
                view.SetAppearance(true, ElementTheme.Dark);
                view.Show();
                Check((GetWindowLongW(WindowNative.GetWindowHandle(view), -20) & 8) != 0, "floating window pinned");
            }
            var editGrid = (Grid)editor.Content;
            var editBox = editGrid.Children.OfType<TextBox>().Single();
            var actions = editGrid.Children.OfType<StackPanel>().Single();
            var apply = actions.Children.OfType<Button>().Single(b => (string)b.Content == "写回原控件");
            var reload = actions.Children.OfType<Button>().Single(b => (string)b.Content == "重新载入");
            var refBox = ((Grid)reference.Content).Children.OfType<TextBox>().Single();
            Check(!editBox.IsReadOnly && refBox.IsReadOnly, "detached editors honor source capabilities");
            Check(editBox.Text == "输入中文" && refBox.Text == "reference", "each detached window displays its own source");
            editBox.Text = "浮窗写回";
            Invoke(apply);
            await Wait(() => !editBox.IsReadOnly && !apply.IsEnabled);
            Check(await writable.ReadAsync() == "浮窗写回", "apply updates the external native control");
            editBox.Text = "未保存的编辑";
            SetFixtureText(hwnd, 1, "外部更新");
            SetFixtureText(hwnd, 2, "只读值也已更新");
            await Wait(() => refBox.Text == "只读值也已更新" && !apply.IsEnabled && reload.IsEnabled);
            Check(editBox.Text == "未保存的编辑" && await writable.ReadAsync() == "外部更新", "conflicting text retained on both sides");
            Invoke(reload);
            await Wait(() => editBox.Text == "外部更新");
            Check(GetForegroundWindow() == foreground, "showing and refreshing detached controls preserve foreground");

            nint remaining = WindowNative.GetWindowHandle(reference);
            editor.Close();
            await Task.Delay(100);
            SetFixtureText(hwnd, 2, "still connected");
            await Wait(() => refBox.Text == "still connected");
            Check(IsWindow(remaining), "closing a detached window keeps its sibling connected");
            reference.SetAppearance(false, ElementTheme.Light);
            Check((GetWindowLongW(remaining, -20) & 8) == 0, "pin changes an existing float");
            reference.Close();
            _log.Add("PASS");
        }
        catch (Exception ex) { _log.Add(ex.ToString()); Environment.ExitCode = 1; }
        finally
        {
            if (!fixture.HasExited) fixture.Kill();
            await File.WriteAllLinesAsync(report, _log);
            foreach (var window in windows.AsEnumerable().Reverse())
            {
                try { if (IsWindow(WindowNative.GetWindowHandle(window))) window.Close(); }
                catch (COMException) { /* The probe may already have closed this window. */ }
            }
            Exit();
        }
    }

    private void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        _log.Add(name);
    }
    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task Wait(Func<bool> ready)
    {
        for (int tries = 0; tries < 80; tries++)
        {
            if (ready()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Detached window did not reflect its native source in time.");
    }

    private static void SetFixtureText(nint window, int id, string text)
    {
        if (SendMessageW(GetDlgItem(window, id), 0x000C /* WM_SETTEXT */, 0, text) == 0)
            throw new InvalidOperationException("Could not update the external test edit control.");
    }

    private static void MakeFixture(string title)
    {
        var instance = GetModuleHandleW(null);
        var root = CreateWindowExW(0, "Static", title, 0x10CF0000, 20, 30, 300, 250, 0, 0, instance, 0);
        CreateWindowExW(0, "Edit", "initial", 0x50000000, 16, 12, 200, 28, root, 1, instance, 0);
        CreateWindowExW(0, "Edit", "reference", 0x50000800, 16, 50, 200, 28, root, 2, instance, 0);
        CreateWindowExW(0, "Edit", "secret fixture", 0x50000020, 16, 88, 200, 28, root, 3, instance, 0);
        CreateWindowExW(0, "Button", "Unsupported", 0x50000000, 16, 126, 200, 28, root, 4, instance, 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint module, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string cls, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, string lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
}
