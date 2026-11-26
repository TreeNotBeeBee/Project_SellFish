using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace WindowPilot;

internal sealed record Shortcut(uint Modifiers, uint Key, byte Sides = 0)
{
    internal bool IsTap => SideBit(Key) != 0 && Modifiers == 0 && Sides == 0;
    internal bool NeedsHook => IsTap || Sides != 0;
    internal bool IsValid => IsTap || (IsMainKey(Key) && Modifiers <= 15 &&
        (Sides == 0 || ModifierFamilies(Sides) == Modifiers));

    // Previous versions stored the RegisterHotKey family flags only. Sides == 0
    // retains their either-side semantics. The bit order is part of the file format.
    internal static readonly uint[] ModifierKeys = [0xA2, 0xA3, 0xA4, 0xA5, 0xA0, 0xA1, 0x5B, 0x5C];
    internal static byte SideBit(uint key) => key switch
    {
        0xA2 => 1, 0xA3 => 2, 0xA4 => 4, 0xA5 => 8,
        0xA0 => 16, 0xA1 => 32, 0x5B => 64, 0x5C => 128, _ => 0
    };
    internal static uint ModifierFamilies(byte sides)
    {
        uint families = 0;
        if ((sides & 3) != 0) families |= 2;
        if ((sides & 12) != 0) families |= 1;
        if ((sides & 48) != 0) families |= 4;
        if ((sides & 192) != 0) families |= 8;
        return families;
    }
    internal bool Overlaps(Shortcut other) => Key == other.Key && Modifiers == other.Modifiers &&
        (Sides == 0 || other.Sides == 0 || Sides == other.Sides);

    // VK_ESCAPE and VK_TAB stay available for cancel and keyboard navigation. F12 is reserved.
    internal static bool IsMainKey(uint key) => key is
        0x08 or 0x0D or 0x13 or 0x20 or >= 0x21 and <= 0x28 or 0x2D or 0x2E or
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or
        >= 0x70 and <= 0x87 and not 0x7B or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Sides != 0)
        {
            foreach (var modifier in ModifierKeys)
                if ((Sides & SideBit(modifier)) != 0) parts.Add(DescribeKey(modifier));
        }
        else
        {
            if ((Modifiers & 2) != 0) parts.Add("Ctrl");
            if ((Modifiers & 1) != 0) parts.Add("Alt");
            if ((Modifiers & 4) != 0) parts.Add("Shift");
            if ((Modifiers & 8) != 0) parts.Add("Win");
        }
        parts.Add(DescribeKey(Key));
        return string.Join(" + ", parts);
    }

    private static string DescribeKey(uint key)
    {
        if (SideBit(key) != 0) return key switch
        {
            0xA2 => "左 Ctrl", 0xA3 => "右 Ctrl", 0xA4 => "左 Alt", 0xA5 => "右 Alt",
            0xA0 => "左 Shift", 0xA1 => "右 Shift", 0x5B => "左 Win", _ => "右 Win"
        };
        if (key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return $"F{key - 0x6F}";
        if (key is >= 0x60 and <= 0x69) return $"Num {key - 0x60}";
        int scanCode = (int)MapVirtualKeyW(key, 4);
        int keyData = (scanCode & 0xFF) << 16;
        if ((scanCode >> 8) == 0xE0) keyData |= 1 << 24;
        var name = new StringBuilder(64);
        return GetKeyNameTextW(keyData, name, name.Capacity) > 0 ? name.ToString() : $"VK {key:X2}";
    }

    internal static Shortcut? Load(string path, Shortcut? fallback)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Shortcut>(File.ReadAllText(path));
            // 没有配置文件时沿用初始快捷键；显式保存的 JSON null 表示用户已清除绑定。
            if (saved == null || saved.IsValid) return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return fallback;
    }

    internal static void Save(string path, Shortcut? shortcut)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(shortcut));
        File.Move(temp, path, true);
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetKeyNameTextW(int keyData, StringBuilder text, int max);
}

/// <summary>Recognizes a solitary modifier release, including repeats and interrupted taps.</summary>
internal sealed class ModifierGesture
{
    internal byte Held { get; private set; }
    private uint _tapKey;

    internal void Reset(byte held) { Held = held; CancelTap(); }
    internal void CancelTap() => _tapKey = 0;
    internal bool Observe(uint key, bool pressed, bool canStartTap)
    {
        byte bit = Shortcut.SideBit(key);
        if (bit == 0) { CancelTap(); return false; }
        if (pressed)
        {
            if ((Held & bit) != 0) return false;
            _tapKey = Held == 0 && canStartTap ? key : 0;
            Held |= bit;
            return false;
        }
        bool complete = _tapKey == key;
        Held &= (byte)~bit;
        CancelTap();
        return complete;
    }
}

/// <summary>Registers the panel's global shortcuts and handles their record and clear controls.</summary>
internal sealed class ShortcutEditor : IDisposable
{
    private sealed class Binding(string name, string file, (TextBox Field, Button Clear, Action Activate) controls,
        Shortcut? fallback, int baseId)
    {
        internal string Name { get; } = name;
        internal string Path { get; } = System.IO.Path.Combine(AppProfile.DirectoryPath, file);
        internal TextBox Field { get; } = controls.Field;
        internal Button ClearButton { get; } = controls.Clear;
        internal Action Activate { get; } = controls.Activate;
        internal int BaseId { get; } = baseId;
        internal Shortcut? Value { get; set; } = Shortcut.Load(System.IO.Path.Combine(AppProfile.DirectoryPath, file), fallback);
        internal Shortcut? ActiveValue => Active ? Value : null;
        internal int RegisteredId { get; set; }
        internal bool Active { get; set; }
    }

    private const uint WmActivate = 0x0006, WmHotkey = 0x0312;
    private const nuint SubclassId = 0x5748;
    private readonly nint _hwnd;
    private readonly Binding[] _bindings;
    private readonly Action<string> _status;
    private readonly SubclassProc _windowProc;
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly PointerEventHandler _pointerHandler;
    private readonly ModifierGesture _gesture = new();
    private readonly HashSet<uint> _consumedKeys = [];
    private nint _keyboardHook, _mouseHook;
    private Binding? _editing;
    private Shortcut? _candidate;
    private bool _finishing;
    private bool _disposed;

    internal ShortcutEditor(nint hwnd, (TextBox, Button, Action) hide, (TextBox, Button, Action) through,
        (TextBox, Button, Action) increase, (TextBox, Button, Action) decrease, Action<string> status)
    {
        _hwnd = hwnd;
        _status = status;
        _bindings =
        [
            new("隐藏", "shortcut.json", hide, new(6, 'H'), 0x4721),
            new("页面穿透", "passthrough-shortcut.json", through, new(6, 'P'), 0x4723),
            new("增加不透明度", "opacity-increase-shortcut.json", increase, null, 0x4725),
            new("减少不透明度", "opacity-decrease-shortcut.json", decrease, null, 0x4727)
        ];
        _windowProc = Receive;
        _keyboardProc = CaptureKey;
        _mouseProc = ObserveMouse;
        _pointerHandler = OnPointer;
        if (!SetWindowSubclass(hwnd, _windowProc, SubclassId, 0))
            throw new InvalidOperationException("无法初始化面板快捷键。");

        foreach (var binding in _bindings)
        {
            try { if (binding.Value is { } saved) Replace(binding, saved, persist: false); }
            catch (Exception ex) { _status(ex.Message); }
            binding.Field.IsReadOnly = true;
            binding.Field.IsTabStop = true;
            binding.Field.PlaceholderText = "按下快捷键";
            binding.Field.GotFocus += OnFocus;
            binding.Field.LostFocus += OnBlur;
            binding.Field.PreviewKeyDown += OnKeyDown;
            binding.Field.AddHandler(UIElement.PointerPressedEvent, _pointerHandler, true);
            binding.ClearButton.Click += OnClearClick;
            ToolTipService.SetToolTip(binding.ClearButton, $"清除{binding.Name}快捷键");
            Display(binding);
        }
    }

    private void Display(Binding binding)
    {
        binding.Field.Text = binding.Value?.ToString() ?? "";
        binding.Field.PlaceholderText = "设置快捷键";
        binding.ClearButton.IsEnabled = binding.Value != null;
        string state = binding.Value != null && !binding.Active ? "已保存的组合未能启用。" : "";
        ToolTipService.SetToolTip(binding.Field, $"{state}点击录入{binding.Name}快捷键，支持单按左 / 右修饰键。Backspace 清除，Esc 取消");
    }

    private void OnClearClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        var binding = _bindings.FirstOrDefault(b => b.ClearButton == button);
        if (binding == null) return;
        EndRecording();
        try { Clear(binding); _status(""); }
        catch (Exception ex) { _status(ex.Message); }
        Display(binding);
    }

    private void Clear(Binding binding)
    {
        // 写入成功后才释放注册；配置保存失败时，原来的快捷键仍可使用。
        Shortcut.Save(binding.Path, null);
        if (binding.RegisteredId != 0) UnregisterHotKey(_hwnd, binding.RegisteredId);
        binding.RegisteredId = 0;
        binding.Active = false;
        binding.Value = null;
        ReleaseUnusedHooks();
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
        if (!_finishing && sender is TextBox field && _editing?.Field == field) EndRecording();
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
        try { EnsureHooks(mouse: true); }
        catch (Win32Exception ex)
        {
            ReleaseUnusedHooks();
            _status(ex.Message);
            return;
        }
        _editing = binding;
        binding.Field.Text = "";
        binding.Field.PlaceholderText = "按下快捷键";
    }

    private void EndRecording()
    {
        _gesture.CancelTap();
        var previous = _editing;
        _editing = null;
        _candidate = null;
        _finishing = false;
        ReleaseUnusedHooks();
        if (previous != null) Display(previous);
    }

    private void EnsureHooks(bool mouse)
    {
        if (_keyboardHook == 0)
        {
            byte held = 0;
            foreach (var key in Shortcut.ModifierKeys)
                if (GetAsyncKeyState((int)key) < 0) held |= Shortcut.SideBit(key);
            _gesture.Reset(held);
            _keyboardHook = InstallHook(13, _keyboardProc);
        }
        if (mouse && _mouseHook == 0) _mouseHook = InstallHook(14, _mouseProc);
    }

    private static nint InstallHook(int kind, HookProc callback)
    {
        nint hook = SetWindowsHookExW(kind, callback, GetModuleHandleW(null), 0);
        if (hook == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听快捷键，请重新点击并录入。");
        return hook;
    }

    private void ReleaseUnusedHooks()
    {
        if (!_disposed && _editing != null) return;
        if (_mouseHook != 0 && (_disposed || !_bindings.Any(b => b.ActiveValue?.IsTap == true)))
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        if (_keyboardHook != 0 && (_disposed || !_bindings.Any(b => b.ActiveValue?.NeedsHook == true)))
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
            _consumedKeys.Clear();
        }
    }

    private nint ObserveMouse(int code, nuint message, nint data)
    {
        // A click, button release or wheel gesture uses the modifier. Mere movement does not.
        if (code >= 0 && message is >= 0x201 and <= 0x20E) _gesture.CancelTap();
        return CallNextHookEx(0, code, message, data);
    }

    private static uint PhysicalKey(nint data)
    {
        uint key = unchecked((uint)Marshal.ReadInt32(data));
        int scan = Marshal.ReadInt32(data, 4);
        // On AltGr layouts Windows pairs right Alt with a simulated left Ctrl.
        // SCANCODE_SIMULATED (0x200) distinguishes it from a physical Ctrl.
        if (key is 0x11 or 0xA2 && scan == 0x21D) return 0;
        // KBDLLHOOKSTRUCT typically supplies a side-specific VK. Some sources send
        // generic VKs instead; distinguish those using the scan code and extended flag.
        return key switch
        {
            0x10 => scan == 0x36 ? 0xA1u : 0xA0u,
            0x11 => (Marshal.ReadInt32(data, 8) & 1) != 0 ? 0xA3u : 0xA2u,
            0x12 => (Marshal.ReadInt32(data, 8) & 1) != 0 ? 0xA5u : 0xA4u,
            _ => key
        };
    }

    private static bool OtherInputHeld()
    {
        // Called only on the initial down edge of a potential tap. Check keys and
        // mouse buttons already held before that edge without retaining typed input.
        for (uint key = 1; key < 255; key++)
        {
            if (key is >= 0x10 and <= 0x12 || Shortcut.SideBit(key) != 0) continue;
            if (GetAsyncKeyState((int)key) < 0) return true;
        }
        return false;
    }

    private nint CaptureKey(int code, nuint message, nint data)
    {
        bool pressed = message is 0x100 or 0x104;
        if (code < 0 || (!pressed && message is not (0x101 or 0x105)))
            return CallNextHookEx(0, code, message, data);
        uint key = PhysicalKey(data);
        if (key == 0) return CallNextHookEx(0, code, message, data);
        bool modifier = Shortcut.SideBit(key) != 0;
        bool recording = _editing != null && GetForegroundWindow() == _hwnd;
        bool tapAllowed = modifier && pressed && _gesture.Held == 0 &&
            (recording || HasTapBinding(key)) && !OtherInputHeld();
        bool tapped = _gesture.Observe(key, pressed, tapAllowed);
        if (_consumedKeys.Contains(key))
        {
            if (!pressed) _consumedKeys.Remove(key);
            return 1;
        }
        if (recording && _editing is { } binding)
        {
            if (RecordEdge(binding, key, pressed, modifier, tapped)) return 1;
        }
        else if (_editing == null)
        {
            foreach (var action in _bindings)
            {
                if (action.ActiveValue is not { NeedsHook: true } shortcut || shortcut.Key != key) continue;
                if (tapped && shortcut.IsTap) QueueActivation(action);
                if (pressed && !modifier && shortcut.Sides == _gesture.Held && GetAsyncKeyState((int)key) >= 0)
                {
                    _consumedKeys.Add(key);
                    QueueActivation(action);
                    return 1;
                }
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    private bool HasTapBinding(uint key)
    {
        foreach (var binding in _bindings)
            if (binding.ActiveValue is { IsTap: true } shortcut && shortcut.Key == key) return true;
        return false;
    }

    private bool RecordEdge(Binding binding, uint key, bool pressed, bool modifier, bool tapped)
    {
        const uint escape = 0x1B, tab = 0x09;
        if (_finishing || key == tab) return false;
        if (modifier)
        {
            if (tapped && _candidate == null) ScheduleFinish(binding, new(0, key));
            // Pass both edges so an ordinary modifier chord and the shell still see them.
            return false;
        }
        if (pressed && (_candidate == null || key == escape))
            _candidate = new(Shortcut.ModifierFamilies(_gesture.Held), key, _gesture.Held);
        if (!pressed && _candidate is { } selected && selected.Key == key)
            ScheduleFinish(binding, selected);
        return true;
    }

    private void ScheduleFinish(Binding binding, Shortcut selected)
    {
        _candidate = selected;
        _finishing = true;
        // Leave all disk IO, registration and UI work outside the hook.
        binding.Field.DispatcherQueue.TryEnqueue(() => Finish(binding, selected));
    }

    private void Finish(Binding binding, Shortcut selected)
    {
        if (_editing != binding || _candidate != selected) return;
        try
        {
            if (selected.Key == 0x1B) return;
            if (selected is { Key: 0x08, Modifiers: 0 })
            {
                Clear(binding);
                _status("");
                return;
            }
            if (!selected.IsValid)
                throw new InvalidOperationException("该按键不可用，请重新点击并录入。");
            Replace(binding, selected, persist: true);
            _status("");
        }
        catch (Exception ex) { _status(ex.Message); }
        finally { EndRecording(); }
    }

    private void Replace(Binding binding, Shortcut shortcut, bool persist)
    {
        if (binding.Active && binding.Value == shortcut) return;
        // 已保存但启动时被占用的组合也属于原操作，避免下次启动时两个操作争抢同一个键。
        var conflict = _bindings.FirstOrDefault(other => other != binding && (persist || other.Active) &&
            other.Value?.Overlaps(shortcut) == true);
        if (conflict != null) throw new InvalidOperationException($"此快捷键已分配给「{conflict.Name}」。");
        int next = binding.RegisteredId == binding.BaseId ? binding.BaseId + 1 : binding.BaseId;
        bool registered = false;
        try
        {
            if (shortcut.NeedsHook) EnsureHooks(shortcut.IsTap);
            // Windows reserves an entire modifier family. Probe for occupied chords
            // but leave the opposite side available to applications and the other bindings.
            bool alreadyOwned = binding.RegisteredId != 0 && binding.Value?.Overlaps(shortcut) == true;
            if (!shortcut.IsTap && !alreadyOwned)
            {
                if (!RegisterHotKey(_hwnd, next, shortcut.Modifiers | 0x4000, shortcut.Key))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法注册{binding.Name}快捷键 {shortcut}，请换一个组合。");
                registered = true;
                if (shortcut.NeedsHook)
                {
                    UnregisterHotKey(_hwnd, next);
                    registered = false;
                }
            }
            if (persist) Shortcut.Save(binding.Path, shortcut);
            if (binding.RegisteredId != 0) UnregisterHotKey(_hwnd, binding.RegisteredId);
            binding.RegisteredId = registered ? next : 0;
            binding.Value = shortcut;
            binding.Active = true;
            registered = false;
        }
        finally
        {
            if (registered) UnregisterHotKey(_hwnd, next);
            ReleaseUnusedHooks();
        }
    }

    private nint Receive(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data)
    {
        if (msg == WmActivate && (wp & 0xFFFF) == 0 && _editing != null && !_finishing) EndRecording();
        if (msg == WmHotkey)
        {
            // 清除后重新绑定可能复用注册 ID；同时核对消息中的组合，忽略先前尚未处理的按键。
            uint key = (uint)((nuint)lp >> 16) & 0xffff;
            uint modifiers = (uint)lp & 0x0f;
            foreach (var binding in _bindings)
            {
                if (binding.RegisteredId == 0 || wp != (nuint)binding.RegisteredId) continue;
                if (_editing == null && !_disposed && binding.ActiveValue is { } active &&
                    active.Key == key && active.Modifiers == modifiers)
                    QueueActivation(binding);
                return 0;
            }
        }
        return DefSubclassProc(hwnd, msg, wp, lp);
    }

    private void QueueActivation(Binding binding)
    {
        var shortcut = binding.ActiveValue;
        binding.Field.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && _editing == null && shortcut != null && ReferenceEquals(binding.ActiveValue, shortcut))
                binding.Activate();
        });
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
            binding.ClearButton.Click -= OnClearClick;
            if (binding.RegisteredId != 0) UnregisterHotKey(_hwnd, binding.RegisteredId);
        }
        RemoveWindowSubclass(_hwnd, _windowProc, SubclassId);
    }

    private delegate nint SubclassProc(nint window, uint msg, nuint wp, nint lp, nuint id, nuint data);
    private delegate nint HookProc(int code, nuint message, nint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint mods, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int kind, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
}
