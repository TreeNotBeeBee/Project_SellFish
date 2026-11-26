using System.Text.Json;

namespace WindowPilot;

internal enum MiniToolbarStyle { Classic, InitialIcons, InputMethod }

internal sealed class MiniToolbarSettings
{
    private sealed record Preferences(string? Style, string[]? Slots);

    private static readonly string[] Defaults = ["hide", "clickThrough", "topmost"];
    private readonly Dictionary<string, string> _actionIds;
    private readonly string _profile;
    private string[] _slots;
    internal MiniToolbarStyle Style { get; private set; }
    internal bool UsesIcons => Style != MiniToolbarStyle.Classic;
    internal IReadOnlyList<string> Slots => _slots;
    internal event Action? Changed;

    internal MiniToolbarSettings(string profile, IEnumerable<string> actions)
    {
        _profile = profile;
        _actionIds = actions.ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
        _slots = Defaults;
        if (!File.Exists(AppearancePath) && !File.Exists(ClassicPath))
        {
            // 首次使用先记住初始图标，再生成经典布局文件，避免下次启动被误判为旧版配置。
            Style = MiniToolbarStyle.InitialIcons;
            try { Save(Style, _slots); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        }
        else if (File.Exists(AppearancePath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(AppearancePath));
                // 1.3.5 用 ime 保存功能图标。继续读取该标识，升级后才能保留原来的外观。
                Style = saved?.Style switch
                {
                    "ime" => MiniToolbarStyle.InitialIcons,
                    "input-method" => MiniToolbarStyle.InputMethod,
                    _ => MiniToolbarStyle.Classic
                };
                if (saved?.Slots is { } slots) _slots = NormalizeSlots(slots);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { /* 沿用旧版对不可读配置的处理，使用经典样式。 */ }
        }
        // 老版本在首次启动时就生成可编辑的经典工具栏配置，包括用户尚未收起窗口的情况。
        if (!File.Exists(ClassicPath)) ReadClassicActionIds();
    }

    private string AppearancePath => Path.Combine(_profile, "mini-appearance.json");
    private string ClassicPath => Path.Combine(_profile, "mini-toolbar.json");

    private string[] NormalizeSlots(string[] configured)
    {
        // 配置来自旧版或手动编辑时，保留每个有效功能的位置，只填补缺失或重复的位置。
        var slots = new string[Defaults.Length];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Min(configured.Length, slots.Length); i++)
            if (configured[i] is { } id && _actionIds.TryGetValue(id, out var known) && used.Add(known))
                slots[i] = known;
        var remaining = new Queue<string>(Defaults.Where(id => !used.Contains(id)));
        for (int i = 0; i < slots.Length; i++)
            slots[i] ??= remaining.Dequeue();
        return slots;
    }

    internal void SetStyle(MiniToolbarStyle style)
    {
        if (style != Style) Save(style, _slots);
    }

    internal void Assign(int position, string action)
    {
        string id = _actionIds[action];
        if (_slots[position] == id) return;
        string[] next = [.. _slots];
        int previous = Array.IndexOf(next, id);
        if (previous >= 0) next[previous] = next[position];
        next[position] = id;
        Save(Style, next);
    }

    private void Save(MiniToolbarStyle style, string[] slots)
    {
        string styleId = style switch
        {
            MiniToolbarStyle.Classic => "classic",
            MiniToolbarStyle.InitialIcons => "ime",
            MiniToolbarStyle.InputMethod => "input-method",
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };
        // 先写入临时文件再替换。保存失败时，已显示的工具栏继续使用上一次配置。
        Directory.CreateDirectory(_profile);
        string temp = AppearancePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Preferences(styleId, slots)));
        File.Move(temp, AppearancePath, true);
        Style = style;
        _slots = slots;
        Changed?.Invoke();
    }

    internal IReadOnlyList<string> GetActionIds() => UsesIcons ? Slots : ReadClassicActionIds();

    private string[] ReadClassicActionIds()
    {
        // 经典工具栏继续读取原来的可变长数组。保留手动排序、空数组以及每次收起时重读的行为。
        string path = ClassicPath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(_profile);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(stream, Defaults, new JsonSerializerOptions { WriteIndented = true });
                return Defaults;
            }
            var configured = JsonSerializer.Deserialize<string?[]>(File.ReadAllText(path));
            if (configured == null) return Defaults;
            var available = configured.OfType<string>().Where(_actionIds.ContainsKey)
                .Select(id => _actionIds[id]).Distinct().ToArray();
            return configured.Length > 0 && available.Length == 0 ? Defaults : available;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return Defaults; }
    }
}
