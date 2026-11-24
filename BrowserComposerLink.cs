namespace WindowPilot;

/// <summary>Serializes edits for one composer and one pairing. Only unsent drafts are coalesced.</summary>
internal sealed class BrowserComposerLink : IDisposable
{
    private sealed record Command(string Type, string Text, string Expected);
    private readonly BrowserBroker _browser;
    private readonly ChatInputWindow _view;
    private Command? _queued;
    private bool _running, _disposed;

    internal BrowserComposerLink(BrowserBroker browser, ChatInputWindow view)
    {
        _browser = browser;
        _view = view;
        view.DraftChanged += Edit;
        view.SendRequested += Send;
    }

    private void Edit(string text, string expected)
    {
        if (_queued?.Type == "send") return;
        _queued = new("draft", text, expected);
        Drain();
    }

    private void Send(string text, string expected)
    {
        _queued = new("send", text, expected);
        Drain();
    }

    private async void Drain()
    {
        if (_running || _disposed) return;
        _running = true;
        try
        {
            while (_queued is { } command && !_disposed)
            {
                _queued = null;
                bool ok = false;
                string message;
                try
                {
                    var reply = await _browser.CommandAsync(command.Type, command.Expected, command.Text);
                    ok = reply.Ok;
                    message = reply.Message;
                }
                catch (Exception ex) { message = ex.Message; }
                if (_disposed) return;
                // Later edits may have been typed before this change reached the webpage.
                // Advance only our own acknowledged baseline. The page still compares
                // the expected draft before each edit or send.
                if (ok && command.Type == "draft" && _queued is { } next && next.Expected == command.Expected)
                    _queued = next with { Expected = command.Text };
                bool canceledSend = !ok && _queued?.Type == "send";
                if (!ok) _queued = null;
                if (command.Type == "send" || canceledSend) _view.CompleteSend(ok, message);
                if (command.Type == "draft") _view.CompleteDraft(ok, message);
            }
        }
        finally { _running = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _queued = null;
        _view.DraftChanged -= Edit;
        _view.SendRequested -= Send;
    }
}
