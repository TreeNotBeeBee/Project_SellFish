using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using static Interop.UIAutomationClient.UIA_ControlTypeIds;
using static Interop.UIAutomationClient.UIA_PropertyIds;

namespace WindowPilot;

// 选中的区域是稳定的祖先元素。网页逐段替换子节点时，下一次读取仍从这个祖先开始。
internal sealed class NativeTextRegion : IDisposable, IUIAutomationEventHandler,
    IUIAutomationStructureChangedEventHandler, IUIAutomationPropertyChangedEventHandler
{
    private sealed record Scope(IUIAutomationElement Element, int[] RuntimeId, string Role, string Title);
    private readonly WindowTarget _target;
    private readonly Scope[] _areas;
    private readonly IUIAutomationElement? _document;
    private readonly string? _documentAddress;
    private int _selected;
    private volatile bool _disposed;
    private IUIAutomationElement? _listening;
    internal event Action? Changed;
    internal IReadOnlyList<string> Scopes { get; }
    internal int SelectedIndex => Volatile.Read(ref _selected);
    internal string Label { get; }
    internal string Key { get; }
    internal string InitialText { get; private set; } = "";

    private NativeTextRegion(WindowTarget target, Scope[] scopes, int selected, IUIAutomationElement? document)
    {
        _target = target;
        _areas = scopes;
        _selected = selected;
        _document = document;
        _documentAddress = NativeControlPicker.DocumentAddress(document);
        Scopes = scopes.Select(s => s.Title).ToArray();
        Label = "输出区域 · " + scopes[selected].Title;
        // 同一篇文章中点到不同位置，可以分别打开浮窗，再为每个浮窗调整它的读取范围。
        Key = $"region:{target.Handle:X}:{string.Join(".", scopes[0].RuntimeId)}";
    }

    internal static NativeTextRegion Create(IUIAutomation api, WindowTarget target, IUIAutomationElement leaf)
    {
        var path = NativeControlPicker.GetSafePath(api, leaf, target);
        var scopes = new List<Scope>();
        IUIAutomationElement? document = null;
        foreach (var item in path.Take(path.Count - 1))
        {
            int type = item.CurrentControlType;
            string role = Role(item.CurrentAriaRole, item.GetCurrentPropertyValue(UIA_LandmarkTypePropertyId));
            if (Skipped(type, role))
                throw new InvalidOperationException("请在要阅读的正文中点选，避开输入框、导航和操作按钮。");
            bool isDocument = type == UIA_DocumentControlTypeId;
            string title = role switch
            {
                "article" => "文章", "log" or "feed" => "消息列表", "main" => "主要内容", "region" => "内容区",
                "paragraph" => "段落", "list" => "列表", "listitem" => "列表项", "heading" => "标题",
                _ when isDocument => "整个文档",
                _ when type == UIA_TextControlTypeId || type == UIA_HyperlinkControlTypeId => "点选文字",
                _ when type == UIA_GroupControlTypeId || type == UIA_CustomControlTypeId ||
                       type == UIA_PaneControlTypeId || type == UIA_ListControlTypeId ||
                       type == UIA_ListItemControlTypeId || type == UIA_TableControlTypeId => "区域",
                _ => ""
            };
            if (title.Length > 0 && item.GetRuntimeId() is { Length: > 0 } id)
            {
                var name = item.CurrentName?.ReplaceLineEndings(" ").Trim();
                if (name is { Length: > 0 }) title += " · " + (name.Length > 32 ? name[..32] + "…" : name);
                else if (title == "区域") title += $"（第 {scopes.Count + 1} 层）";
                scopes.Add(new(item, id, role, title));
            }
            // 文档之上是浏览器工具栏。范围列表不跨过当前文档，也不穿入嵌套文档。
            if (isDocument) { document = item; break; }
        }
        if (scopes.Count == 0) throw new InvalidOperationException("这里没有可选择的文本区域。");
        int selected = scopes.FindIndex(s => s.Role is "article" or "log" or "feed" or "region");
        if (selected < 0) selected = scopes.FindIndex(s => s.Role == "main");
        if (selected < 0) selected = scopes.FindIndex(s => s.Element.CurrentControlType == UIA_GroupControlTypeId);
        if (selected < 0) selected = 0;
        var areas = scopes.ToArray();
        var region = new NativeTextRegion(target, areas, selected, document);
        try { region.InitialText = region.Read(api, selected); }
        catch (RegionSizeException) when (selected > 0)
        {
            // 长文档仍可从落点开始阅读，在浮窗内挑选适合的祖先，不必盲目再次点选。
            selected = 0;
            region = new NativeTextRegion(target, areas, selected, document);
            region.InitialText = region.Read(api, selected);
        }
        region.Watch(api, scopes[selected].Element);
        return region;
    }

    internal Task<string> SelectScopeAsync(int index) => UiaThread.ReadRegion(api =>
    {
        if ((uint)index >= (uint)_areas.Length) throw new ArgumentOutOfRangeException(nameof(index));
        string text = Read(api, index);
        if (index == SelectedIndex) return text;
        Unwatch(api);
        Volatile.Write(ref _selected, index);
        Watch(api, _areas[index].Element);
        return text;
    });

    internal Task<string> ReadAsync() => UiaThread.ReadRegion(api => Read(api, SelectedIndex));

    private string Read(IUIAutomation api, int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var area = _areas[index];
        var path = NativeControlPicker.GetSafePath(api, area.Element, _target);
        var document = path.FirstOrDefault(e => e.CurrentControlType == UIA_DocumentControlTypeId);
        bool sameDocument = _document == null ? document == null : document != null &&
            api.CompareElements(document, _document) != 0 && NativeControlPicker.DocumentAddress(document) == _documentAddress;
        if (!sameDocument || !area.RuntimeId.SequenceEqual(area.Element.GetRuntimeId()))
            throw NativeControlPicker.InvalidTarget();
        return new RegionReader(api).Read(area.Element);
    }

    private static string Role(string? aria, object landmark) => landmark switch
    {
        UIA_LandmarkTypeIds.UIA_MainLandmarkTypeId => "main",
        UIA_LandmarkTypeIds.UIA_NavigationLandmarkTypeId => "navigation",
        UIA_LandmarkTypeIds.UIA_SearchLandmarkTypeId => "search",
        UIA_LandmarkTypeIds.UIA_FormLandmarkTypeId => "form",
        _ => aria?.ToLowerInvariant() ?? ""
    };

    private static bool Skipped(int type, string role) =>
        type is UIA_EditControlTypeId or UIA_ButtonControlTypeId or UIA_CheckBoxControlTypeId or
            UIA_ComboBoxControlTypeId or UIA_RadioButtonControlTypeId or UIA_MenuBarControlTypeId or
            UIA_MenuControlTypeId or UIA_MenuItemControlTypeId or UIA_ToolBarControlTypeId or
            UIA_ScrollBarControlTypeId or UIA_SliderControlTypeId or UIA_SpinnerControlTypeId or UIA_TabControlTypeId ||
        role is "navigation" or "search" or "form" or "banner" or "complementary" or "contentinfo" or
                "toolbar" or "button" or "textbox" or "searchbox" or "dialog" or "alertdialog";

    // 单次批量取一个容器的直接子元素及其属性。先跳过交互子树，避免读取编辑框、密码和按钮上的文案。
    private sealed class RegionReader
    {
        private const int MaxNodes = 6000, MaxText = 256 * 1024;
        private readonly IUIAutomationCondition _raw;
        private readonly IUIAutomationCacheRequest _cache;
        private readonly StringBuilder _text = new();
        private bool _break;
        private int _nodes;

        internal RegionReader(IUIAutomation api)
        {
            _raw = api.RawViewCondition;
            _cache = api.CreateCacheRequest();
            _cache.TreeFilter = _raw;
            foreach (var property in new[] { UIA_ControlTypePropertyId, UIA_AriaRolePropertyId, UIA_NamePropertyId,
                         UIA_IsPasswordPropertyId, UIA_LandmarkTypePropertyId, UIA_IsTextPatternAvailablePropertyId })
                _cache.AddProperty(property);
        }

        internal string Read(IUIAutomationElement root)
        {
            Visit(root.BuildUpdatedCache(_cache), 0, false);
            return _text.ToString();
        }

        private void Visit(IUIAutomationElement item, int depth, bool inline)
        {
            if (++_nodes > MaxNodes || depth > 64) throw new RegionSizeException();
            int type = item.CachedControlType;
            string role = Role(item.CachedAriaRole, item.GetCachedPropertyValue(UIA_LandmarkTypePropertyId));
            if (item.CachedIsPassword != 0 || Skipped(type, role) || (depth > 0 && type == UIA_DocumentControlTypeId)) return;
            if (type is UIA_TextControlTypeId or UIA_HyperlinkControlTypeId && item.CachedName is { Length: > 0 } name)
            {
                if (!inline) _break = true;
                Append(name);
                if (!inline) _break = true;
                return; // Chromium 同时暴露 StaticText 和字符级子节点，只取一次可见文字。
            }
            bool block = role is "paragraph" or "heading" or "listitem" or "cell" or "rowheader" or "columnheader"
                or "code" or "pre" or "blockquote" || type == UIA_ListItemControlTypeId;
            if (block) _break = true;
            var children = item.FindAllBuildCache(TreeScope.TreeScope_Children, _raw, _cache);
            if (children.Length > MaxNodes - _nodes) throw new RegionSizeException();
            if (children.Length == 0 && item.GetCachedPropertyValue(UIA_IsTextPatternAvailablePropertyId) is true)
                AppendLeafDocument(item);
            for (int i = 0; i < children.Length; i++) Visit(children.GetElement(i), depth + 1, inline || block);
            if (block) _break = true;
        }

        private void AppendLeafDocument(IUIAutomationElement item)
        {
            // 有些只读提供方仅实现 TextPattern；只在范围不含任何嵌入控件时读取，避免卷入控件值。
            if (item.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is not IUIAutomationTextPattern pattern) return;
            var range = pattern.DocumentRange;
            if (range.GetChildren().Length != 0 ||
                range.GetAttributeValue(UIA_TextAttributeIds.UIA_IsReadOnlyAttributeId) is false) return;
            string value = range.GetText(MaxText - _text.Length + 1);
            if (!string.IsNullOrEmpty(value)) Append(value);
        }

        private void Append(string value)
        {
            if (_text.Length + value.Length + 1 > MaxText) throw new RegionSizeException();
            if (_break && _text.Length > 0 && _text[^1] != '\n' && value[0] != '\n') _text.Append('\n');
            _break = false;
            _text.Append(value);
        }
    }

    private sealed class RegionSizeException() : InvalidOperationException("所选区域过大，请选择范围较小的文本或段落。") { }

    private void Watch(IUIAutomation api, IUIAutomationElement element)
    {
        _listening = element;
        // 事件只提示“需要刷新”。回调不访问 UIA 或 WinUI；浮窗合并通知，计时器补上提供方漏报的变化。
        TryEvent(() => api.AddStructureChangedEventHandler(element, TreeScope.TreeScope_Subtree, null, this));
        TryEvent(() => api.AddPropertyChangedEventHandler(element, TreeScope.TreeScope_Subtree, null, this, [UIA_NamePropertyId]));
        TryEvent(() => api.AddAutomationEventHandler(UIA_EventIds.UIA_Text_TextChangedEventId, element, TreeScope.TreeScope_Subtree, null, this));
    }

    private void Unwatch(IUIAutomation api)
    {
        if (_listening is not { } element) return;
        _listening = null;
        TryEvent(() => api.RemoveStructureChangedEventHandler(element, this));
        TryEvent(() => api.RemovePropertyChangedEventHandler(element, this));
        TryEvent(() => api.RemoveAutomationEventHandler(UIA_EventIds.UIA_Text_TextChangedEventId, element, this));
    }

    private static void TryEvent(Action action)
    {
        try { action(); }
        catch (COMException) { /* 某些提供方只支持轮询，移除已销毁的元素也可能失败。 */ }
    }
    private void ChangedInProvider() { if (!_disposed) Changed?.Invoke(); }
    public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) => ChangedInProvider();
    public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType change, int[] runtimeId) => ChangedInProvider();
    public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue) => ChangedInProvider();

    public void Dispose()
    {
        _disposed = true;
        _ = UiaThread.ReadRegion(api => { Unwatch(api); return true; });
    }
}
