using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowPilot;

internal sealed record Shortcut(uint Modifiers, uint Key)
{
    internal static Shortcut Default => new(6, 'H');
    internal static readonly string[] Keys = BuildKeys();

    private static string[] BuildKeys()
    {
        var names = new System.Collections.Generic.List<string>();
        for (char key = 'A'; key <= 'Z'; key++) names.Add(key.ToString());
        for (char key = '0'; key <= '9'; key++) names.Add(key.ToString());
        for (int n = 1; n <= 11; n++) names.Add($"F{n}");
        return names.ToArray();
    }

    internal string KeyName => Key >= 0x70 && Key <= 0x7A ? $"F{Key - 0x6F}" : ((char)Key).ToString();
    internal bool IsValid => Modifiers is > 0 and <= 15 && Array.IndexOf(Keys, KeyName) >= 0 && FromName(KeyName) == Key;
    internal static uint FromName(string value) => value.Length == 1 ? value[0] : 0x6Fu + uint.Parse(value.AsSpan(1));
    public override string ToString() =>
        ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") +
        ((Modifiers & 4) != 0 ? "Shift + " : "") + ((Modifiers & 8) != 0 ? "Win + " : "") + KeyName;

    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowPilot", "shortcut.json");
    internal static Shortcut Load()
    {
        try
        {
            var shortcut = JsonSerializer.Deserialize<Shortcut>(File.ReadAllText(SettingsFile));
            if (shortcut is { IsValid: true }) return shortcut;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return Default;
    }
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile + ".tmp", JsonSerializer.Serialize(this));
        File.Move(SettingsFile + ".tmp", SettingsFile, true);
    }
}

internal sealed class GlobalShortcut : IDisposable
{
    private const uint WmHotkey = 0x312;
    private const nuint SubclassId = 0x5748;
    private readonly nint _window;
    private readonly SubclassProc _proc;
    private readonly Action _pressed;
    private int _id;
    internal Shortcut? Active { get; private set; }

    internal GlobalShortcut(nint window, Action pressed)
    {
        _window = window;
        _pressed = pressed;
        _proc = Receive;
        if (!SetWindowSubclass(window, _proc, SubclassId, 0))
            throw new InvalidOperationException("无法初始化全局快捷键。");
    }

    internal void Register(Shortcut shortcut)
    {
        if (!shortcut.IsValid) throw new InvalidOperationException("请选择至少一个修饰键和一个主键。");
        if (Active == shortcut) return;
        int next = _id == 0x4721 ? 0x4722 : 0x4721;
        if (!RegisterHotKey(_window, next, shortcut.Modifiers | 0x4000, shortcut.Key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法注册 {shortcut}，请换一个组合。");
        if (_id != 0) UnregisterHotKey(_window, _id);
        _id = next;
        Active = shortcut;
    }

    private nint Receive(nint window, uint msg, nuint wp, nint lp, nuint id, nuint data)
    {
        if (msg == WmHotkey && wp == (nuint)_id)
        {
            _pressed();
            return 0;
        }
        return DefSubclassProc(window, msg, wp, lp);
    }
    public void Dispose()
    {
        if (_id != 0) UnregisterHotKey(_window, _id);
        RemoveWindowSubclass(_window, _proc, SubclassId);
        Active = null;
        _id = 0;
    }

    private delegate nint SubclassProc(nint window, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint mods, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
