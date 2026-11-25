using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowPilot;

internal sealed partial class NativeControlBinding(WindowTarget target, IUIAutomationElement element) : INativeTextSource
{
    private const int MaxTextLength = 256 * 1024;
    private volatile bool _closed;
    private int[] _id = [];
    private IUIAutomationElement? _document;
    private string? _webAddress;
    private bool _proseMirror;
    public string Key { get; private set; } = "";
    public string Label { get; private set; } = "";
    internal string InitialText { get; private set; } = "";
    internal bool Writable { get; private set; }

    internal void Initialize(IUIAutomation api)
    {
        _id = element.GetRuntimeId();
        if (_id is not { Length: > 0 }) throw NativeControlPicker.InvalidTarget();
        Key = target.Handle.ToString("X") + ":" + string.Join(".", _id);
        _document = DocumentFor(NativeControlPicker.GetSafePath(api, element, target));
        _webAddress = NativeControlPicker.DocumentAddress(_document);
        _proseMirror = _document != null && (element.CurrentClassName ?? "").Split(' ').Contains("ProseMirror");
        var value = ValuePattern();
        Writable = value != null && value.CurrentIsReadOnly == 0 && element.CurrentIsEnabled != 0 &&
            element.CurrentControlType == UIA_ControlTypeIds.UIA_EditControlTypeId;
        InitialText = Read(api);
        Label = LabelFor(api);
    }

    private string LabelFor(IUIAutomation api)
    {
        bool edit = element.CurrentControlType == UIA_ControlTypeIds.UIA_EditControlTypeId;
        string name = "";
        try
        {
            if (element.CurrentLabeledBy is { } label)
            {
                NativeControlPicker.GetSafePath(api, label, target);
                name = label.CurrentName ?? "";
            }
            if (string.IsNullOrWhiteSpace(name) && edit) name = element.CurrentName ?? "";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* A label is optional. */ }
        string kind = edit ? "输入框" : "文本";
        name = name.Trim();
        if (name.Length == 0 || name == InitialText.Trim() || name.Contains('\n') || name.Contains('\r')) return kind;
        return kind + " · " + (name.Length <= 24 ? name : name[..24] + "…");
    }

    private IUIAutomationValuePattern? ValuePattern() =>
        element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) as IUIAutomationValuePattern;

    private List<IUIAutomationElement> Validate(IUIAutomation api)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var path = NativeControlPicker.GetSafePath(api, element, target);
        if (!_id.SequenceEqual(element.GetRuntimeId())) throw NativeControlPicker.InvalidTarget();
        var doc = DocumentFor(path);
        if (!SameDocument(api, _document, doc) || (_webAddress != null && NativeControlPicker.DocumentAddress(doc) != _webAddress))
            throw new InvalidOperationException("原页面已切换，请重新选择控件。");
        return path;
    }

    private static IUIAutomationElement? DocumentFor(IEnumerable<IUIAutomationElement> path) =>
        path.FirstOrDefault(item => item.CurrentControlType == UIA_ControlTypeIds.UIA_DocumentControlTypeId);

    private static bool SameDocument(IUIAutomation api, IUIAutomationElement? first, IUIAutomationElement? second) =>
        first == null ? second == null : second != null && api.CompareElements(first, second) != 0;

    private string Read(IUIAutomation api)
    {
        Validate(api);
        string text;
        // 编辑框的值就是草稿，非编辑文档的 Value 却可能只是地址；文档正文由 TextPattern 提供。
        if (element.CurrentControlType != UIA_ControlTypeIds.UIA_DocumentControlTypeId && ValuePattern() is { } controlValue)
            text = controlValue.CurrentValue ?? "";
        else if (element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern document)
            text = document.DocumentRange.GetText(MaxTextLength + 1) ?? "";
        else if (element.CurrentControlType == UIA_ControlTypeIds.UIA_TextControlTypeId) text = element.CurrentName ?? "";
        else if (_webAddress == null && ValuePattern() is { } nativeValue) text = nativeValue.CurrentValue ?? "";
        else throw new InvalidOperationException("这个控件没有公开可读取的文本。");
        if (text.Length > MaxTextLength) throw new InvalidOperationException("控件文字过长，请选择较小的文本区域。");
        if (_proseMirror && text.EndsWith('\n') && HasTrailingFiller(api, text)) text = text[..^1];
        return text;
    }

    private bool HasTrailingFiller(IUIAutomation api, string text)
    {
        // Chromium 把 ProseMirror 的占位 <br> 也暴露为一个换行。按树中的标记判断，
        // 不能 Trim：用户可能有意从空行起笔，或以一个真实的空段落收尾。
        var walker = api.RawViewWalker;
        var last = walker.GetLastChildElement(element);
        for (int depth = 0; last != null && depth < 16; depth++, last = walker.GetLastChildElement(last))
        {
            if (last.CurrentClassName != "ProseMirror-trailingBreak") continue;
            if (text.All(c => c == '\n')) return true;
            var preceding = walker.GetPreviousSiblingElement(last);
            return preceding?.CurrentName == "\n" && preceding.CurrentClassName != "ProseMirror-trailingBreak";
        }
        return false;
    }

    internal Task<string> ReadAsync() => UiaThread.Run(Read);
    internal async Task<string> WriteAsync(string expected, string replacement)
    {
        if (replacement.Length > MaxTextLength) throw new InvalidOperationException("输入内容过长。");
        await UiaThread.Run(api =>
        {
            string current = Read(api);
            if (current != expected && current != replacement)
                throw new InvalidOperationException("原控件已更改。请先重新载入原文，再编辑写回。");
            var value = ValuePattern();
            if (!Writable || value == null || value.CurrentIsReadOnly != 0 || element.CurrentIsEnabled == 0)
                throw new InvalidOperationException("该控件当前不能写入。");
            ObjectDisposedException.ThrowIf(_closed, this);
            if (current != replacement)
            {
                // Blink 用 innerText 替换富文本。末尾单个 <br> 会被编辑器解释成占位，
                // 加一个渲染占位后，用户写的末尾换行才能进入编辑器模型。
                string payload = _proseMirror && replacement.EndsWith('\n') ? replacement + "\n" : replacement;
                value.SetValue(payload);
            }
            return true;
        }).ConfigureAwait(false);
        // 网页应用还要消化 DOM 变更。只发一次写入，等待回读；不重复覆盖可能同时输入的文字。
        int attempts = _document == null ? 1 : 8;
        for (int i = 0; i < attempts; i++)
        {
            if (_document != null) await Task.Delay(90).ConfigureAwait(false);
            string actual = await ReadAsync().ConfigureAwait(false);
            if (actual == replacement) return actual;
        }
        throw new InvalidOperationException("原应用未接受完整文字。请核对原控件，浮窗草稿已保留。");
    }

    public void Dispose() => _closed = true;
}
