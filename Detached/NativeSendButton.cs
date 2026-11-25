using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowPilot;

internal sealed class SendButtonSelectionRequiredException(string message)
    : InvalidOperationException("草稿已写回，尚未发送。" + message);
internal sealed class SendOutcomeUnknownException(string message, Exception cause) : InvalidOperationException(message, cause);

internal sealed partial class NativeControlBinding
{
    private sealed record SelectedButton(IUIAutomationElement Element, int[] RuntimeId, string Name);
    private volatile SelectedButton? _button;
    private int _sending;
    internal bool HasSendButton => _button != null;

    internal async Task AttachSendButtonAsync(CancellationToken token)
    {
        var point = await NativeControlPicker.PickPointAsync(target, token);
        await AttachSendButtonAtAsync(point.X, point.Y, token);
    }

    internal Task AttachSendButtonAtAsync(int x, int y, CancellationToken token) => UiaThread.Run(api =>
    {
        token.ThrowIfCancellationRequested();
        var leaf = NativeControlPicker.ElementAt(api, target, x, y);
        var path = NativeControlPicker.GetSafePath(api, leaf, target);
        var button = path.FirstOrDefault(item => item.CurrentControlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId);
        if (button == null) throw new InvalidOperationException("请点选原窗口中实际用于发送的按钮。");
        token.ThrowIfCancellationRequested();
        AttachSendButton(api, button);
        return true;
    });

    internal void AttachSendButton(IUIAutomation api, IUIAutomationElement button)
    {
        Validate(api);
        var path = NativeControlPicker.GetSafePath(api, button, target);
        if (!Writable || button.CurrentControlType != UIA_ControlTypeIds.UIA_ButtonControlTypeId ||
            button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern)
            throw new InvalidOperationException("这个按钮没有公开可直接调用的操作。");
        if (!SameDocument(api, _document, DocumentFor(path)))
            throw new InvalidOperationException("请选择与原输入框位于同一页面的按钮。");
        var id = button.GetRuntimeId();
        if (id is not { Length: > 0 }) throw NativeControlPicker.InvalidTarget();
        _button = new(button, id, button.CurrentName ?? "");
    }

    internal async Task SendAsync(string expected, string replacement)
    {
        if (string.IsNullOrWhiteSpace(replacement)) throw new InvalidOperationException("请先填写要发送的文字。");
        if (Interlocked.Exchange(ref _sending, 1) != 0) throw new InvalidOperationException("上一次发送仍在进行。");
        try
        {
            await WriteAsync(expected, replacement).ConfigureAwait(false);
            // 某些表单在草稿更新后才显示或启用发送按钮。可以重读按钮状态，但绝不重试 Invoke。
            for (int attempt = 0; attempt < 5; attempt++)
            {
                bool sent = await UiaThread.Run(api => TrySend(api, replacement, attempt == 4)).ConfigureAwait(false);
                if (sent) return;
                await Task.Delay(110).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _sending, 0); }
    }

    private bool TrySend(IUIAutomation api, string expected, bool lastAttempt)
    {
        var path = Validate(api);
        var button = _button is { } selected ? ResolveSelected(api, selected) : FindFormButton(api, path);
        if (button == null || button.CurrentIsEnabled == 0)
        {
            if (!lastAttempt) return false;
            if (button == null) throw new SendButtonSelectionRequiredException("请点「选择发送按钮」，再点原窗口中的发送按钮。");
            throw new InvalidOperationException("草稿已写回，尚未发送。原窗口的发送按钮还不可用。");
        }
        if (Read(api) != expected) throw new InvalidOperationException("发送前原输入框已改变，草稿已保留。");
        // 网页可以把同一按钮改作「停止生成」。读回草稿后再核对动作，避免慢速 provider 造成误点。
        string? name = button.CurrentName;
        if ((_button is { } bound ? name != bound.Name : !IsSendName(name)) || button.CurrentIsEnabled == 0 ||
            button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern action)
            throw new SendButtonSelectionRequiredException("发送按钮已改变，请核对原窗口后重选。");
        ObjectDisposedException.ThrowIf(_closed, this);
        try { action.Invoke(); }
        catch (COMException ex)
        {
            throw new SendOutcomeUnknownException("按钮调用结果不明确。请在原窗口核对后再决定是否发送。", ex);
        }
        return true;
    }

    private IUIAutomationElement ResolveSelected(IUIAutomation api, SelectedButton selected)
    {
        try
        {
            var button = selected.Element;
            if (selected.RuntimeId.SequenceEqual(button.GetRuntimeId()) && (button.CurrentName ?? "") == selected.Name &&
                button.CurrentControlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId &&
                SameDocument(api, _document, DocumentFor(NativeControlPicker.GetSafePath(api, button, target)))) return button;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* 原按钮被重新渲染。 */ }
        throw new SendButtonSelectionRequiredException("原发送按钮已变更。请重新选择发送按钮。");
    }

    private IUIAutomationElement? FindFormButton(IUIAutomation api, List<IUIAutomationElement> path)
    {
        // 仅在原输入框所属的表单/撰写器内自动发现；整张网页或整扇窗口可能有其他会话的发送按钮。
        if (_document == null) return null;
        foreach (var container in path.Skip(1))
        {
            if (api.CompareElements(container, _document) != 0) break;
            if (!IsComposer(container)) continue;
            var edits = Controls(api, container, UIA_ControlTypeIds.UIA_EditControlTypeId);
            if (edits.Length != 1 || api.CompareElements(edits.GetElement(0), element) == 0) return null;
            var buttons = Controls(api, container, UIA_ControlTypeIds.UIA_ButtonControlTypeId);
            IUIAutomationElement? match = null;
            for (int i = 0; i < buttons.Length; i++)
            {
                var b = buttons.GetElement(i);
                if (!IsSendName(b.CurrentName)) continue;
                if (match != null) throw new SendButtonSelectionRequiredException("表单内有多个发送按钮，请手动选择。");
                if (SameDocument(api, _document, DocumentFor(NativeControlPicker.GetSafePath(api, b, target)))) match = b;
            }
            if (match != null) return match;
        }
        return null;
    }

    private static IUIAutomationElementArray Controls(IUIAutomation api, IUIAutomationElement parent, int type) =>
        parent.FindAll(TreeScope.TreeScope_Descendants, api.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, type));

    private static bool IsComposer(IUIAutomationElement element) =>
        element.CurrentAriaRole == "form" || element.CurrentAutomationId is "composer-background" or "thread-bottom-container" ||
        (element.CurrentClassName ?? "").Split(' ').Any(token => token.StartsWith("composer-", StringComparison.OrdinalIgnoreCase));

    private static bool IsSendName(string? name) => name?.Trim().ToLowerInvariant() is
        "发送" or "发送消息" or "发送提示" or "发送提示词" or "send" or "send message" or "send prompt";
}
