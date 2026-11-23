using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace WindowPilot;

internal sealed record Shortcut(uint Modifiers, uint Key)
{
    internal bool IsValid => Modifiers <= 15 && IsMainKey(Key);

    // VK_ESCAPE and VK_TAB stay available for cancel and keyboard navigation. F12 is reserved.
    internal static bool IsMainKey(uint key) => key is
        0x08 or 0x0D or 0x13 or 0x20 or >= 0x21 and <= 0x28 or 0x2D or 0x2E or
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or
        >= 0x70 and <= 0x87 and not 0x7B or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(DescribeKey(Key));
        return string.Join(" + ", parts);
    }

    private static string DescribeKey(uint key)
    {
        if (key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return $"F{key - 0x6F}";
        if (key is >= 0x60 and <= 0x69) return $"Num {key - 0x60}";
        int scanCode = (int)MapVirtualKeyW(key, 4);
        int keyData = (scanCode & 0xFF) << 16;
        if ((scanCode >> 8) == 0xE0) keyData |= 1 << 24;
        var name = new StringBuilder(64);
        return GetKeyNameTextW(keyData, name, name.Capacity) > 0 ? name.ToString() : $"VK {key:X2}";
    }

    internal static Shortcut Load(string path, Shortcut fallback)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Shortcut>(File.ReadAllText(path));
            if (saved is { IsValid: true }) return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return fallback;
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, true);
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetKeyNameTextW(int keyData, StringBuilder text, int max);
}

/// <summary>Owns two independent OS hotkeys and their single-chord recorder fields.</summary>
internal sealed class ShortcutEditor : IDisposable
{
    private sealed class Binding(string name, string file, TextBox field, Action activate, Shortcut fallback, int baseId)
    {
        internal string Name { get; } = name;
        internal string Path { get; } = System.IO.Path.Combine(AppProfile.DirectoryPath, file);
        internal TextBox Field { get; } = field;
        internal Action Activate { get; } = activate;
        internal int BaseId { get; } = baseId;
        internal Shortcut Value { get; set; } = Shortcut.Load(System.IO.Path.Combine(AppProfile.DirectoryPath, file), fallback);
        internal int RegisteredId { get; set; }
    }

    private const uint WmActivate = 0x0006, WmHotkey = 0x0312;
    private const nuint SubclassId = 0x5748;
    private readonly nint _hwnd;
    private readonly Binding[] _bindings;
    private readonly Action<string> _status;
    private readonly SubclassProc _windowProc;
    private readonly KeyboardProc _keyboardProc;
    private readonly PointerEventHandler _pointerHandler;
    private nint _keyboardHook;
    private Binding? _editing;
    private Shortcut? _candidate;
    private bool _finishing;
    private bool _disposed;

    internal ShortcutEditor(nint hwnd, TextBox hideBox, TextBox clickThroughBox,
        Action toggleHide, Action toggleClickThrough, Action<string> status)
    {
        _hwnd = hwnd;
        _status = status;
        _bindings =
        [
            new("隐藏", "shortcut.json", hideBox, toggleHide, new(6, 'H'), 0x4721),
            new("页面穿透", "passthrough-shortcut.json", clickThroughBox, toggleClickThrough, new(6, 'P'), 0x4723)
        ];
        _windowProc = Receive;
        _keyboardProc = CaptureKey;
        _pointerHandler = OnPointer;
        if (!SetWindowSubclass(hwnd, _windowProc, SubclassId, 0))
            throw new InvalidOperationException("无法初始化面板快捷键。");

        foreach (var binding in _bindings)
        {
            try { Replace(binding, binding.Value, persist: false); }
            catch (Exception ex) { _status(ex.Message); }
            binding.Field.IsReadOnly = true;
            binding.Field.IsTabStop = true;
            binding.Field.PlaceholderText = "按下快捷键";
            binding.Field.GotFocus += OnFocus;
            binding.Field.LostFocus += OnBlur;
            binding.Field.PreviewKeyDown += OnKeyDown;
            binding.Field.AddHandler(UIElement.PointerPressedEvent, _pointerHandler, true);
            Display(binding);
        }
    }

    private void Display(Binding binding)
    {
        binding.Field.Text = binding.RegisteredId == 0 ? "" : binding.Value.ToString();
        binding.Field.PlaceholderText = binding.RegisteredId == 0 ? "设置快捷键" : "按下快捷键";
        ToolTipService.SetToolTip(binding.Field, $"点击录入{binding.Name}快捷键，Esc 取消");
    }

    private Binding? ForField(object sender) =>
        sender is TextBox field ? _bindings.FirstOrDefault(binding => binding.Field == field) : null;

    private void OnFocus(object sender, RoutedEventArgs args)
    {
        if (ForField(sender) is { } binding && binding.Field.FocusState == FocusState.Keyboard)
            Begin(binding);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (ForField(sender) is not { } binding || !args.GetCurrentPoint(binding.Field).Properties.IsLeftButtonPressed) return;
        binding.Field.Focus(FocusState.Pointer);
        Begin(binding);
    }

    private void OnBlur(object sender, RoutedEventArgs args)
    {
        if (sender is TextBox field && _editing?.Field == field) EndRecording();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_editing == null && args.Key is VirtualKey.Enter or VirtualKey.Space &&
            ForField(sender) is { } binding && !args.KeyStatus.WasKeyDown)
        {
            args.Handled = true;
            Begin(binding);
        }
    }

    private void Begin(Binding binding)
    {
        if (_disposed || _editing == binding) return;
        EndRecording();
        // Only the focused recorder needs a hook. WM_HOTKEY otherwise diverts existing
        // chords away from the field, sometimes to another application.
        _keyboardHook = SetWindowsHookExW(13, _keyboardProc, GetModuleHandleW(null), 0);
        if (_keyboardHook == 0)
        {
            _status("无法开始录入快捷键，请重新点击。");
            return;
        }
        _editing = binding;
        binding.Field.Text = "";
        binding.Field.PlaceholderText = "按下快捷键";
    }

    private void EndRecording()
    {
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = 0;
        var previous = _editing;
        _editing = null;
        _candidate = null;
        _finishing = false;
        if (previous != null) Display(previous);
    }

    private nint CaptureKey(int code, nuint message, nint data)
    {
        if (code < 0 || _editing is not { } binding || GetForegroundWindow() != _hwnd)
            return CallNextHookEx(0, code, message, data);

        uint key = unchecked((uint)Marshal.ReadInt32(data));
        const uint escape = 0x1B, tab = 0x09;
        if (key == tab || key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
            return CallNextHookEx(0, code, message, data);
        bool pressed = message is 0x100 or 0x104;
        bool released = message is 0x101 or 0x105;
        if (!pressed && !released) return CallNextHookEx(0, code, message, data);
        if (_finishing) return 1;
        if (pressed && (_candidate == null || key == escape))
            _candidate = new Shortcut(ReadModifiers(), key);
        if (released && _candidate is { } selected && selected.Key == key)
        {
            _finishing = true;
            // File IO, UI callbacks and registration must run outside the low-level hook.
            binding.Field.DispatcherQueue.TryEnqueue(() => Finish(binding, selected));
        }
        return 1;
    }

    private void Finish(Binding binding, Shortcut selected)
    {
        if (_editing != binding || _candidate != selected) return;
        try
        {
            if (selected.Key == 0x1B) return;
            if (!selected.IsValid)
                throw new InvalidOperationException("该按键不可用，请重新点击并录入。");
            if (_bindings.Any(other => other != binding && other.RegisteredId != 0 && other.Value == selected))
                throw new InvalidOperationException("两个操作需要使用不同的快捷键。");
            Replace(binding, selected, persist: true);
            _status("");
        }
        catch (Exception ex) { _status(ex.Message); }
        finally { EndRecording(); }
    }

    private void Replace(Binding binding, Shortcut shortcut, bool persist)
    {
        if (binding.RegisteredId != 0 && binding.Value == shortcut) return;
        int next = binding.RegisteredId == binding.BaseId ? binding.BaseId + 1 : binding.BaseId;
        if (!RegisterHotKey(_hwnd, next, shortcut.Modifiers | 0x4000, shortcut.Key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法注册{binding.Name}快捷键 {shortcut}，请换一个组合。");
        try { if (persist) shortcut.Save(binding.Path); }
        catch
        {
            UnregisterHotKey(_hwnd, next);
            throw;
        }
        if (binding.RegisteredId != 0) UnregisterHotKey(_hwnd, binding.RegisteredId);
        binding.RegisteredId = next;
        binding.Value = shortcut;
    }

    private nint Receive(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data)
    {
        if (msg == WmActivate && (wp & 0xFFFF) == 0 && _editing != null) EndRecording();
        if (msg == WmHotkey)
        {
            foreach (var binding in _bindings)
            {
                if (binding.RegisteredId == 0 || wp != (nuint)binding.RegisteredId) continue;
                if (_editing == null && !_disposed)
                    binding.Field.DispatcherQueue.TryEnqueue(() => { if (!_disposed && _editing == null) binding.Activate(); });
                return 0;
            }
        }
        return DefSubclassProc(hwnd, msg, wp, lp);
    }

    private static uint ReadModifiers()
    {
        uint mods = 0;
        if (GetAsyncKeyState(0x12) < 0) mods |= 1;
        if (GetAsyncKeyState(0x11) < 0) mods |= 2;
        if (GetAsyncKeyState(0x10) < 0) mods |= 4;
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) mods |= 8;
        return mods;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndRecording();
        foreach (var binding in _bindings)
        {
            binding.Field.GotFocus -= OnFocus;
            binding.Field.LostFocus -= OnBlur;
            binding.Field.PreviewKeyDown -= OnKeyDown;
            binding.Field.RemoveHandler(UIElement.PointerPressedEvent, _pointerHandler);
            if (binding.RegisteredId != 0) UnregisterHotKey(_hwnd, binding.RegisteredId);
        }
        RemoveWindowSubclass(_hwnd, _windowProc, SubclassId);
    }

    private delegate nint SubclassProc(nint window, uint msg, nuint wp, nint lp, nuint id, nuint data);
    private delegate nint KeyboardProc(int code, nuint message, nint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint mods, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int kind, KeyboardProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
}
