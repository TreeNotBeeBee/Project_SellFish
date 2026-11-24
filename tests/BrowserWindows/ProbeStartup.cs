using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace WindowPilot;

// Compiled in a separate test directory in place of App.xaml.cs. The release
// executable never includes this startup or its foreground test substitute.
public partial class App : Application
{
    private MainWindow? _panel;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string result = Path.Combine(AppProfile.DirectoryPath, "desktop-result.txt");
        try
        {
            _panel = new MainWindow();
            _panel.AppWindow.Show(false);
            await _panel.ExerciseBrowserWindows();
            File.WriteAllText(result, "PASS");
        }
        catch (Exception ex) { File.WriteAllText(result, ex.ToString()); }
        finally { _panel?.Close(); Exit(); }
    }
}

public sealed partial class MainWindow
{
    internal async Task ExerciseBrowserWindows()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var folder = AppProfile.DirectoryPath;
        async Task Wait(Func<bool> check, string description)
        {
            while (!check())
            {
                if (timeout.IsCancellationRequested) throw new Exception("Timed out: " + description + " / " + _browser?.Status);
                await Task.Delay(40);
            }
        }
        void Require(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            File.AppendAllText(Path.Combine(folder, "assertions.txt"), description + Environment.NewLine);
        }
        void CheckNoActivation(nint before, string description)
        {
            nint after = GetForegroundWindow();
            GetWindowThreadProcessId(after, out uint foregroundPid);
            // Other programs can activate during this test on a shared desktop.
            // Only a transition to our own new foreground HWND is a focus steal.
            Require(before == after || foregroundPid != Environment.ProcessId, description);
        }
        // Directly arm an isolated broker. The fixture runs headless and has no
        // Windows HWND; production foreground checks are covered in BridgeTests.
        _browser = new BrowserBroker(action => DispatcherQueue.TryEnqueue(() => action()), _ => true);
        _browser.Changed += OnWebChanged;
        string name = Environment.GetEnvironmentVariable("SELL_FISH_TEST_BROWSER") == "edge" ? "msedge" : "chrome";
        _browser.BeginPairing(new(0, 0, 0, "", "Headless fixture", name));
        File.WriteAllText(Path.Combine(folder, "armed"), "");
        await Wait(() => _browser.Connected && _browser.State != null, "initial fixture state");
        nint focus = GetForegroundWindow();
        OpenAnswerView(this, new RoutedEventArgs());
        OpenInputView(this, new RoutedEventArgs());
        await Wait(() => _answers != null && _composer != null && _detached.Count == 2, "two native slots");
        CheckNoActivation(focus, "opening both floating windows does not activate a sell fish window");
        ToolPin.IsChecked = true;
        SetToolPin(this, new RoutedEventArgs());
        static bool OnTop(Window window) => (GetWindowLongW(WindowNative.GetWindowHandle(window), -20) & 8) != 0;
        Require(OnTop(this) && OnTop(_answers!) && OnTop(_composer!), "pin on the panel and both actual HWNDs");
        ToolPin.IsChecked = false;
        SetToolPin(this, new RoutedEventArgs());
        Require(!OnTop(this) && !OnTop(_answers!) && !OnTop(_composer!), "unpin on all three HWNDs");
        File.WriteAllText(Path.Combine(folder, "windows-ready"), "");
        await Wait(() => File.Exists(Path.Combine(folder, "background")), "browser fixture switched to another tab and minimized");
        var draft = (TextBox)FindControl(_composer!.Content, "ChatDraft")!;
        var send = (Button)FindControl(_composer.Content, "SendChatDraft")!;
        var answer = (RichTextBlock)FindControl(_answers!.Content, "ChatAnswers")!;
        const string message = "你好，世界。\n这是第二行。";
        draft.Text = message;
        await Wait(() => _browser.State?.Draft == message && send.IsEnabled, "both draft lines acknowledged");
        ((IInvokeProvider)new ButtonAutomationPeer(send).GetPattern(PatternInterface.Invoke)).Invoke();
        nint focusDuringStream = GetForegroundWindow();
        await Wait(() => answer.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>())
            .Any(run => run.Text.Contains("流式完成", StringComparison.Ordinal)), "streamed answer reached actual RichTextBlock");
        var runs = answer.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToList();
        Require(answer.IsTextSelectionEnabled && answer.Blocks.Count >= 3 &&
            runs.Any(run => run.Text.Contains("alpha") && run.FontFamily.Source == "Consolas") &&
            runs.Any(run => run.Text.Contains("1. 第一项\n2. 第二项")),
            "answer is selectable and preserves streaming, list and code text");
        CheckNoActivation(focusDuringStream, "streamed page updates do not activate a sell fish window");
        _answers.Close();
        await Wait(() => _answers == null && _composer != null && _detached.Count == 1, "closed one of two windows");
        Require(_browser.Connected, "closing just the answer leaves the composer paired");
        OpenAnswerView(this, new RoutedEventArgs());
        await Wait(() => _detached.Count == 2, "reopened answer");
        Release();
        await Wait(() => _detached.Count == 0 && _browser == null, "all detached sessions released");
        Require(_answers == null && _composer == null, "release closes both views and forgets the connection");
    }

    private static DependencyObject? FindControl(DependencyObject root, string id)
    {
        if (AutomationProperties.GetAutomationId(root) == id) return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindControl(VisualTreeHelper.GetChild(root, i), id) is { } element) return element;
        return null;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
}
