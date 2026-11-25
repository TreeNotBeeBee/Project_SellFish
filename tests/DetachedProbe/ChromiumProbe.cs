using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Interop.UIAutomationClient;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using WinRT.Interop;

namespace WindowPilot;

public partial class App
{
    // The local page reports ProseMirror's document, independently of UIA's SetValue return value.
    private async Task RunChromiumFixtureAsync()
    {
        var report = Environment.GetEnvironmentVariable("SELL_FISH_DETACHED_RESULT")!;
        Window? reader = null, writer = null;
        try
        {
            var hwnd = (nint)long.Parse(Environment.GetEnvironmentVariable("SELL_FISH_CHROMIUM_HWND")!);
            var points = Environment.GetEnvironmentVariable("SELL_FISH_CHROMIUM_POINTS")!.Split(',').Select(int.Parse).ToArray();
            uint thread = GetWindowThreadProcessId(hwnd, out uint pid);
            using var browser = Process.GetProcessById((int)pid);
            var target = new WindowTarget(hwnd, pid, thread, "Chrome_WidgetWin_1", "sell fish UIA fixture",
                browser.ProcessName, browser.StartTime.ToUniversalTime().Ticks);
            var origin = new Uri(Environment.GetEnvironmentVariable("SELL_FISH_CHROMIUM_ORIGIN")!);
            if (origin.Scheme != "http" || origin.Host != "127.0.0.1") throw new ArgumentException("Expected local fixture");
            using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(2) };

            using var input = (NativeControlBinding)await NativeControlPicker.LocateAsync(target, points[0], points[1], default);
            Check(input.Writable && input.InitialText == "", "empty rich editor has no phantom prefix");
            if (Environment.GetEnvironmentVariable("SELL_FISH_CHROMIUM_SEND") != "automatic")
                await input.AttachSendButtonAtAsync(points[2], points[3], default);
            using var document = (NativeTextRegion)await NativeControlPicker.LocateAsync(target, points[6], points[7], default);
            Check(document.InitialText.Contains("First paragraph") && document.InitialText.Contains("Third paragraph") &&
                !document.InitialText.Contains(origin.Host), "ordinary point selection reads the document body, never its accessibility URL");
            using var region = (NativeTextRegion)await NativeControlPicker.LocateAsync(target, points[4], points[5], default);
            Check(region.InitialText.Contains("First paragraph") && region.InitialText.Contains("Second paragraph")
                && !region.InitialText.Contains(origin.Host), "ordinary paragraph selection immediately includes article body");
            // Simulate a coarse Chromium hit on the document, while preserving the real text click and provider tree.
            using var coarse = await UiaThread.ReadRegion(api =>
            {
                var leaf = NativeControlPicker.ElementAt(api, target, points[4], points[5]);
                var web = NativeControlPicker.GetSafePath(api, leaf, target).First(e => e.CurrentControlType == UIA_ControlTypeIds.UIA_DocumentControlTypeId);
                return (NativeTextRegion)NativeControlPicker.LocateElement(api, target, web, points[4], points[5], outputRegion: true);
            });
            Check(coarse.InitialText.Contains("Second paragraph") && coarse.Scopes.Any(scope => scope.Contains("First answer")),
                "coarse Document hit recovers clicked article and explicit output scopes");
            _log.Add("Scopes: " + string.Join(" | ", region.Scopes));
            int all = region.Scopes.ToList().FindIndex(label => label.Contains("Conversation output", StringComparison.Ordinal));
            Check(all >= 0, "selected paragraph exposes its conversation ancestor");
            reader = new NativeRegionWindow(region);
            writer = new NativeTextWindow(input);
            reader.AppWindow.Show(false);
            writer.AppWindow.Show(false);

            var readerGrid = ((Grid)reader.Content).Children.OfType<Grid>().Single();
            var ranges = readerGrid.Children.OfType<ComboBox>().Single();
            var output = (RichTextBlock)readerGrid.Children.OfType<ScrollViewer>().Single().Content;
            string VisibleText() => ((Run)((Paragraph)output.Blocks[0]).Inlines[0]).Text;
            ranges.SelectedIndex = all;
            await Wait(() => VisibleText().Contains("Third paragraph", StringComparison.Ordinal));
            Check(VisibleText().Contains("First paragraph") && VisibleText().Contains("Second paragraph") &&
                !VisibleText().Contains("Outside navigation") && !VisibleText().Contains("Unrelated button"),
                "region float includes both turns and omits page controls");
            var editor = ((Grid)writer.Content).Children.OfType<TextBox>().Single();
            var options = ((Grid)writer.Content).Children.OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "NativeTextOptions");
            var commands = (MenuFlyout)options.Flyout;
            var apply = commands.Items.OfType<MenuFlyoutItem>().Single(i => AutomationProperties.GetAutomationId(i) == "ApplyNativeText");

            foreach (string value in new[] {"第一次写回", "第二次写回", "\n故意保留开头换行", "故意保留末尾换行\n", "两处\n\n空行\n\n中间"})
            {
                editor.Text = value;
                await Wait(() => apply.IsEnabled);
                new MenuFlyoutItemAutomationPeer(apply).Invoke();
                await Wait(() => !editor.IsReadOnly && !apply.IsEnabled);
                await WaitForPage(http, state => state.GetProperty("draft").GetString() == value);
            }
            Check(true, "repeated writes preserve exact text and intentional leading whitespace");

            const string message = "第一行\n第二行";
            writer.Activate();
            editor.Focus(FocusState.Programmatic);
            editor.Text = "第一行";
            editor.SelectionStart = editor.Text.Length;
            await PostReturnAsync(writer, shift: true);
            await Wait(() => editor.Text.ReplaceLineEndings("\n") == "第一行\n");
            Check((await WaitForPage(http, _ => true)).GetProperty("sent").GetArrayLength() == 0, "Shift+Enter creates a newline without sending");
            editor.SelectedText = "第二行";
            Check(editor.Text.ReplaceLineEndings("\n") == message, "actual floating editor contains both lines");
            int notifications = 0;
            region.Changed += () => Interlocked.Increment(ref notifications);
            await PostReturnAsync(writer, shift: false);
            await WaitForPage(http, state => state.GetProperty("sent").GetArrayLength() == 1);
            await Wait(() => VisibleText().Contains("Streaming complete", StringComparison.Ordinal));
            var final = await WaitForPage(http, _ => true);
            Check(final.GetProperty("sent").GetArrayLength() == 1 && final.GetProperty("sent")[0].GetString() == message
                && final.GetProperty("decoys").GetInt32() == 0, "first plain Enter sends both lines to the page exactly once");
            Check(VisibleText().Contains("Updated third paragraph") && !VisibleText().Contains("Third paragraph"),
                "live region float follows replacement of complete article subtree");
            Check(Volatile.Read(ref notifications) > 0, "browser raised a native UIA change notification in the selected output region");
            _log.Add("PASS");
        }
        catch (Exception ex) { _log.Add(ex.ToString()); Environment.ExitCode = 1; }
        finally
        {
            await File.WriteAllLinesAsync(report, _log);
            reader?.Close();
            writer?.Close();
            Exit();
        }
    }

    // Address only the focused WinUI input site inside the disposable desktop. System-wide SendInput
    // would type into the user's active desktop instead of testing the isolated floating window.
    private static async Task PostReturnAsync(Window writer, bool shift)
    {
        await Task.Delay(80);
        nint focus = GetFocus();
        if (focus == 0 || GetAncestor(focus, 2) != WindowNative.GetWindowHandle(writer))
            throw new InvalidOperationException("The temporary floating editor did not own keyboard focus");
        byte[] saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new InvalidOperationException("Could not read thread keyboard state");
        var state = (byte[])saved.Clone();
        if (shift) state[0x10] = state[0xA0] = 0x80;
        try
        {
            if (!SetKeyboardState(state) || !PostMessageW(focus, 0x0100, 0x0D, 0x001C0001))
                throw new InvalidOperationException("Could not post Return to the isolated editor");
            await Task.Delay(200);
            PostMessageW(focus, 0x0101, 0x0D, unchecked((int)0xC01C0001));
            await Task.Delay(50);
        }
        finally { SetKeyboardState(saved); }
    }

    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] keys);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    private static async Task<JsonElement> WaitForPage(HttpClient client, Func<JsonElement, bool> ready)
    {
        for (int i = 0; i < 80; i++)
        {
            using var response = JsonDocument.Parse(await client.GetStringAsync("/state"));
            if (ready(response.RootElement)) return response.RootElement.Clone();
            await Task.Delay(100);
        }
        throw new TimeoutException("Actual editor model did not reach the expected state.");
    }
}
