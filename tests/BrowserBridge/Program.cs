using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WindowPilot;

string temp = Directory.CreateTempSubdirectory("sell-fish-bridge-test-").FullName;
Environment.SetEnvironmentVariable("WINDOWPILOT_PROFILE_DIR", temp);
var target = new WindowTarget(0, 0, 0, "", "test", "chrome");
const string session = "801816be-6bb5-4099-91d4-a7c29350d350";
using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
CancellationToken token = limit.Token;
int tests = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException(label);
    Console.WriteLine("PASS " + label);
    tests++;
}
async Task Wait(Func<bool> accept)
{
    while (!accept()) await Task.Delay(15, token);
}
Process Host()
{
    var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, BrowserWire.HostExe))
    { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
    info.ArgumentList.Add(BrowserWire.Origin);
    return Process.Start(info)!;
}
async Task Message(Process host, object value) =>
    await BrowserWire.WriteAsync(host.StandardInput.BaseStream, JsonSerializer.SerializeToUtf8Bytes(value), token);
async Task<JsonElement> Receive(Process host)
{
    var bytes = await BrowserWire.ReadAsync(host.StandardOutput.BaseStream, token) ?? throw new EndOfStreamException();
    using var json = JsonDocument.Parse(bytes);
    return json.RootElement.Clone();
}
async Task<JsonElement> Pair(Process host)
{
    await Message(host, new { type = "pair", session, browser = "chrome", windowId = 1, tabId = 1, documentId = "document" });
    return await Receive(host);
}
Task State(Process host, string conversation, string draft) => Message(host, new { type = "state", session, conversation, draft,
    canSend = draft.Length > 0, generating = false, blocks = new[] { new { kind = "code", text = "一\n二" } } });

try
{
    using (var noPanel = Host())
        Check((await Pair(noPanel)).GetProperty("type").GetString() == "reject", "requires an armed desktop panel");
    using (var realGuard = new BrowserBroker(action => action()))
    {
        realGuard.BeginPairing(target);
        using var wrongWindow = Host();
        Check((await Pair(wrongWindow)).GetProperty("type").GetString() == "reject", "rejects unbound foreground HWND");
    }
    using var broker = new BrowserBroker(action => action(), _ => true);
    broker.BeginPairing(target);
    // A client from a different executable cannot consume a valid pairing.
    using (var impostor = new NamedPipeClientStream(".", BrowserWire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
    {
        await impostor.ConnectAsync(token);
        byte[]? response = null;
        try { response = await BrowserWire.ReadAsync(impostor, token); } catch (IOException) { }
        Check(response == null, "requires the packaged native host executable");
    }
    using var page = Host();
    Check((await Pair(page)).GetProperty("type").GetString() == "accept", "bidirectional framed host IPC");
    await State(page, "new", "");
    await Wait(() => broker.State?.Conversation == "new");
    Check(broker.Connected && broker.State!.Blocks.Single().Text == "一\n二", "pins document and reads semantic content");
    Check(!(await broker.CommandAsync("send", "stale", "incorrect")).Ok, "rejects stale draft before sending");
    var request = broker.CommandAsync("send", "", "第一行\n第二行");
    var send = await Receive(page);
    Check(send.GetProperty("type").GetString() == "send" && send.GetProperty("text").GetString() == "第一行\n第二行",
        "multiline submission received once");
    Check(!(await broker.CommandAsync("send", "", "第一行\n第二行")).Ok, "blocks duplicate simultaneous submit");
    await Message(page, new { type = "result", session, id = send.GetProperty("id").GetString(), ok = true, message = "" });
    Check((await request).Ok, "matches acknowledgement to pending command");
    await State(page, "c/first", "");
    await Wait(() => broker.State?.Conversation == "c/first");
    Check(broker.Connected, "own first send may promote a new chat");
    await State(page, "c/other", "private");
    await Wait(() => !broker.Connected);
    Check(broker.State?.Conversation == "c/first" && !(await broker.CommandAsync("send", "", "wrong")).Ok,
        "changing existing conversations pauses and isolates old drafts");
    broker.Disconnect();
    await page.WaitForExitAsync(token);
    Check(broker.State == null, "release terminates child host and clears browser state");
    broker.BeginPairing(target);
    using var page2 = Host();
    Check((await Pair(page2)).GetProperty("type").GetString() == "accept", "reconnect starts a fresh pairing");
    await State(page2, "new", "keep");
    await Wait(() => broker.State?.Draft == "keep");
    await State(page2, "c/stranger", "wrong");
    await Wait(() => !broker.Connected);
    Check(broker.State?.Draft == "keep", "unrequested new-chat transition preserves the current draft");
    broker.Disconnect();
    await page2.WaitForExitAsync(token);
    using var invalidLength = new MemoryStream(new byte[] { 255, 255, 255, 127 });
    try { await BrowserWire.ReadAsync(invalidLength, token); throw new Exception("Oversize frame accepted."); }
    catch (InvalidDataException) { Check(true, "rejects oversized frames before allocating"); }
    Console.WriteLine($"{tests} passed");
}
finally { Directory.Delete(temp); }
