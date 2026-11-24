using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace WindowPilot;

// This probe creates its own external native controls and uses no browser account.
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

            nint foreground = GetForegroundWindow();
            var input = new ChatInputWindow();
            var answers = new ChatAnswerWindow();
            var native = new NativeTextWindow(writable);
            foreach (var window in new DetachedWindow[] { input, answers, native })
            {
                windows.Add(window);
                window.SetAppearance(true, ElementTheme.Dark);
                window.Show();
                Check((GetWindowLongW(WindowNative.GetWindowHandle(window), -20) & 8) != 0, "floating window pinned");
            }
            input.SetConnected(true, "");
            input.SetRemoteDraft("first", true);
            var box = ((Grid)input.Content).Children.OfType<TextBox>().Single();
            var row = ((Grid)input.Content).Children.OfType<StackPanel>().Single();
            var send = row.Children.OfType<Button>().Single(b => (string)b.Content == "发送");
            var page = row.Children.OfType<Button>().Single(b => (string)b.Content == "采用网页草稿");
            int sent = 0, drafts = 0;
            input.SendRequested += (_, _) => sent++;
            input.DraftChanged += (_, _) => drafts++;
            box.Text = "local";
            for (int tries = 0; drafts == 0 && tries < 60; tries++) await Task.Delay(80);
            Check(drafts == 1, "local drafts are debounced");
            input.SetRemoteDraft("concurrent", true);
            Check(box.Text == "local" && !send.IsEnabled && page.Visibility == Visibility.Visible, "concurrent web edit retains local");
            Invoke(page);
            await Task.Delay(80);
            Check(box.Text == "concurrent", "use web draft resolves conflict");
            Invoke(send);
            await Task.Delay(80);
            Check(sent == 1 && !send.IsEnabled && box.IsReadOnly, "in-flight send is single-shot");
            input.CompleteSend(false, "test disconnect");
            input.SetConnected(false, "disconnected");
            Check(box.Text == "concurrent" && !send.IsEnabled, "uncertain send retains draft");
            answers.SetAnswers((AnswerBlock[])[new("paragraph", "回答"), new("list", "项目"), new("code", "print(1)")]);
            var viewport = ((Grid)answers.Content).Children.OfType<ScrollViewer>().Single();
            var rich = (RichTextBlock)viewport.Content;
            Check(rich.Blocks.Count == 3 && rich.IsTextSelectionEnabled, "selectable paragraphs, list and code");
            for (int n = 0; n < 8; n++)
                answers.SetAnswers((AnswerBlock[])[new("paragraph", "回答"), new("list", "项目"), new("code", $"print({n})")]);
            var history = Enumerable.Range(0, 100).Select(n => new AnswerBlock("paragraph", $"line {n}")).ToList();
            answers.SetAnswers(history);
            await Task.Delay(180);
            Check(viewport.ScrollableHeight > 0 && Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) < 4,
                "streaming follows the end");
            viewport.ChangeView(null, 80, null, true);
            await Task.Delay(120);
            var offset = viewport.VerticalOffset;
            history.Add(new("paragraph", "next message"));
            answers.SetAnswers(history);
            await Task.Delay(120);
            Check(Math.Abs(viewport.VerticalOffset - offset) < 4, "scroll position preserved while reading history");
            Check(GetForegroundWindow() == foreground, "opening and streaming preserve foreground");
            nint other = WindowNative.GetWindowHandle(answers);
            input.Close();
            await Task.Delay(100);
            Check(IsWindow(other), "closing input leaves answer window alive");
            answers.SetAppearance(false, ElementTheme.Light);
            Check((GetWindowLongW(other, -20) & 8) == 0, "pin changes existing float");
            native.Close();
            answers.Close();
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
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
}
