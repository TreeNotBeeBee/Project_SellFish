using System.Text.Json;

namespace WindowPilot;

internal enum MiniToolbarStyle { InitialIcons, InputMethod }

internal sealed class MiniToolbarSettings
{
    internal const int MaxActions = 4;
    private sealed record Preferences(string? Style, string?[]? Slots);

    private static readonly string[] Defaults = ["hide", "clickThrough", "topmost"];
    private readonly Dictionary<string, string> _actionIds;
    private readonly string _profile;
    private string[] _slots;
    internal MiniToolbarStyle Style { get; private set; }
    internal IReadOnlyList<string> Slots => _slots;
    internal event Action? Changed;

    internal MiniToolbarSettings(string profile, IEnumerable<string> actions)
    {
        _profile = profile;
        _actionIds = actions.ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
        _slots = Defaults.Where(_actionIds.ContainsKey).ToArray();
        try
        {
            var saved = Read<Preferences>(AppearancePath);
            Style = saved?.Style == "input-method" ? MiniToolbarStyle.InputMethod : MiniToolbarStyle.InitialIcons;
            // 经典样式的实际按钮顺序单独保存在旧文件中。旧 ime 样式已有自己的 Slots。
            var configured = saved?.Slots;
            if (saved == null || saved.Style == "classic")
                configured = Read<string?[]>(Path.Combine(profile, "mini-toolbar.json")) ?? configured;
            if (configured != null)
            {
                var valid = configured.OfType<string>().Where(_actionIds.ContainsKey)
                    .Select(id => _actionIds[id]).Distinct().Take(MaxActions).ToArray();
                // 显式保存的空数组表示只留下拖动和展开。损坏的非空列表才回到默认按钮。
                if (configured.Length == 0 || valid.Length != 0) _slots = valid;
            }
            if (saved == null || saved.Style != StyleId(Style) || saved.Slots == null || !saved.Slots.SequenceEqual(_slots))
                Save(Style, _slots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* 只读配置目录仍可用当前已读取的配置启动。 */ }
    }

    private string AppearancePath => Path.Combine(_profile, "mini-appearance.json");

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    private static string StyleId(MiniToolbarStyle style) => style switch
    {
        MiniToolbarStyle.InitialIcons => "initial",
        MiniToolbarStyle.InputMethod => "input-method",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    internal void SetStyle(MiniToolbarStyle style)
    {
        if (style != Style) Save(style, _slots);
    }

    internal void SetSelected(string action, bool selected)
    {
        string id = _actionIds[action];
        if (_slots.Contains(id) == selected) return;
        if (selected && _slots.Length >= MaxActions)
            throw new InvalidOperationException($"工具栏最多可选 {MaxActions} 项功能。");
        // 重新勾选会追加到末尾；移除其中一项时，其余功能的顺序保持不变。
        string[] next = selected ? [.. _slots, id] : _slots.Where(slot => slot != id).ToArray();
        Save(Style, next);
    }

    private void Save(MiniToolbarStyle style, string[] slots)
    {
        var preferences = new Preferences(StyleId(style), slots);
        Directory.CreateDirectory(_profile);
        string temp = AppearancePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(preferences));
        File.Move(temp, AppearancePath, true);
        Style = style;
        _slots = slots;
        Changed?.Invoke();
    }
}
