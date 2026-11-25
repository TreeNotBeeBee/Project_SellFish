using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

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

            using var input = await NativeControlPicker.LocateAsync(target, points[0], points[1], default);
            Check(input.Writable && input.InitialText == "", "empty rich editor has no phantom prefix");
            if (Environment.GetEnvironmentVariable("SELL_FISH_CHROMIUM_SEND") != "automatic")
                await input.AttachSendButtonAtAsync(points[2], points[3], default);
            using var region = await NativeControlPicker.LocateRegionAsync(target, points[4], points[5], default);
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
            var actions = ((Grid)writer.Content).Children.OfType<StackPanel>().Single();
            var apply = actions.Children.OfType<Button>().Single(b => (string)b.Content == "写回原控件");
            var send = actions.Children.OfType<Button>().Single(b => (string)b.Content == "发送");

            foreach (string value in new[] {"第一次写回", "第二次写回", "\n故意保留开头换行", "故意保留末尾换行\n", "两处\n\n空行\n\n中间"})
            {
                editor.Text = value;
                await Wait(() => apply.IsEnabled);
                Invoke(apply);
                await Wait(() => !editor.IsReadOnly && !apply.IsEnabled);
                await WaitForPage(http, state => state.GetProperty("draft").GetString() == value);
            }
            Check(true, "repeated writes preserve exact text and intentional leading whitespace");

            const string message = "第一行\n第二行";
            editor.Text = message;
            await Wait(() => send.IsEnabled);
            int notifications = 0;
            region.Changed += () => Interlocked.Increment(ref notifications);
            Invoke(send);
            await WaitForPage(http, state => state.GetProperty("sent").GetArrayLength() == 1);
            await Wait(() => VisibleText().Contains("Streaming complete", StringComparison.Ordinal));
            var final = await WaitForPage(http, _ => true);
            Check(final.GetProperty("sent").GetArrayLength() == 1 && final.GetProperty("sent")[0].GetString() == message
                && final.GetProperty("decoys").GetInt32() == 0, "one native button invocation sends both lines to the page once");
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
