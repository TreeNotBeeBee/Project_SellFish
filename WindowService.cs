using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowPilot;

public sealed record WindowTarget(nint Handle, uint ProcessId, uint ThreadId, string ClassName,
    string Title, string ProcessName, long ProcessStartTicks = 0)
{
    public string Display => $"{Title}  ·  {ProcessName}  (PID {ProcessId})";
}

/// <summary>
/// Controls one foreign desktop window. Bind/Release preserve original properties.
/// A GUID window property prevents a recycled HWND from receiving a stale restore.
/// </summary>
public sealed class WindowService : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000;
    private const uint LwaAlpha = 2;
    private readonly string _journalPath;
    private Snapshot? _snapshot;
    private BackdropOpacityLayer? _backdrop;
    private bool _preferBackdrop, _zeroPass;
    private string? _backdropFailure;
    private bool _reportedBackdropFailure;
    public bool UsesBackdrop => _backdrop != null && _snapshot?.RequestedAlpha is < 255;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public WindowService(string? journalPath = null, bool recoverPrevious = true)
    {
        _journalPath = journalPath ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "WindowPilot", "recovery.json");
        if (recoverPrevious)
        {
            try { RecoverPreviousSession(); }
            catch (Exception ex) { RecoveryWarning = ex.Message; }
        }
    }

    public string? RecoveryWarning { get; private set; }
    public WindowTarget? Target => _snapshot?.Target;
    public bool IsAlive() => _snapshot != null && SnapshotAlive(_snapshot);
    public bool IsHidden => IsAlive() && !Native.IsWindowVisible(Target!.Handle);
    public bool IsTopmost => IsAlive() && (ReadStyle(Target!.Handle) & WsExTopmost) != 0;
    public bool CanClickThrough => _snapshot is { } s && (s.OriginallyLayered || s.OpacitySupported);
    public bool IsClickThrough => _snapshot?.RequestedClickThrough ?? (_zeroPass ? _snapshot is { OriginallyLayered: true, OriginallyTransparent: true } : IsAlive() &&
        (ReadStyle(Target!.Handle) & (WsExLayered | WsExTransparent)) == (WsExLayered | WsExTransparent));
    public int? Opacity
    {
        get
        {
            if (!IsAlive()) return null;
            if (_preferBackdrop && _snapshot?.RequestedAlpha is byte requested) return (int)Math.Round(requested * 100d / 255);
            var hwnd = Target!.Handle;
            if ((GetClassStyle(hwnd) & 0x60) != 0) return null;
            if ((ReadStyle(hwnd) & WsExLayered) == 0) return 100;
            if (!Native.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)) return null;
            return (flags & LwaAlpha) != 0 ? (int)Math.Round(alpha * 100d / 255) : 100;
        }
    }

    public List<WindowTarget> Enumerate()
    {
        var windows = new List<WindowTarget>();
        var cache = new Dictionary<uint, (string Name, long Started)>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                var target = ReadTarget(hwnd, cache);
                if (target != null) windows.Add(target);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { }
            return true;
        }, 0);
        // Keep the bound window selectable after SW_HIDE removes it from normal enumeration.
        if (IsAlive() && windows.All(x => x.Handle != Target!.Handle)) windows.Add(Target!);
        return windows.OrderBy(x => x.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public WindowTarget? GetForegroundTarget() => ReadTarget(Native.GetForegroundWindow(), null);

    public void Bind(WindowTarget target)
    {
        if (_snapshot != null && SameIdentity(_snapshot.Target, target) && IsAlive()) return;
        if (!IdentityAlive(target)) throw new InvalidOperationException("目标窗口已关闭或被替换，请刷新列表。");
        if (target.ProcessId == Environment.ProcessId) throw new InvalidOperationException("不能绑定工具自身的窗口。");
        Release(); // Restore the old binding before accepting a new one.
        var style = ReadStyle(target.Handle);
        var placement = ReadPlacement(target.Handle);
        var snapshot = new Snapshot
        {
            Identity = Identity.From(target),
            Marker = "WindowPilot.Managed." + Guid.NewGuid().ToString("N"),
            OriginalTopmost = (style & WsExTopmost) != 0,
            OriginallyLayered = (style & WsExLayered) != 0,
            OriginallyTransparent = (style & WsExTransparent) != 0,
            OriginalVisible = Native.IsWindowVisible(target.Handle),
            HidePlacement = PlacementData.From(placement),
            OpacitySupported = (GetClassStyle(target.Handle) & 0x60) == 0
        };
        if (snapshot.OriginallyLayered)
        {
            snapshot.OpacitySupported &= Native.GetLayeredWindowAttributes(target.Handle,
                out var color, out var alpha, out var flags);
            snapshot.ColorKey = color;
            snapshot.Alpha = alpha;
            snapshot.LayeredFlags = flags;
        }
        if (!Native.SetProp(target.Handle, snapshot.Marker, (nint)1)) throw Failure("标记窗口以支持安全恢复");
        _snapshot = snapshot;
        // DingTalk repaints its own layered surface during mouse interaction on some versions.
        // Select independent composition at the first adjustment to avoid even an initial flash.
        _preferBackdrop = string.Equals(target.ProcessName, "DingTalk", StringComparison.OrdinalIgnoreCase)
            && (!snapshot.OriginallyLayered || (snapshot.LayeredFlags & LwaAlpha) == 0 || snapshot.Alpha == 255);
        try { Persist(); }
        catch
        {
            Native.RemoveProp(target.Handle, snapshot.Marker);
            _snapshot = null;
            throw;
        }
    }

    public void SetTopmost(bool enabled)
    {
        var snapshot = RequireTarget();
        if (IsTopmost == enabled) return;
        snapshot.TopmostChanged = true;
        Persist();
        SetTopmostNative(snapshot.Target, enabled);
        if (_backdrop != null) ApplyManagedAppearance(snapshot);
    }

    public void SetOpacity(int percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent), "不透明度须在 0% 到 100% 之间。");
        SetOpacityByte((byte)Math.Round(percent * 255d / 100));
    }

    /// <summary>Exact alpha control for the effects compositor; alpha 1 retains native hit testing.</summary>
    public void SetOpacityByte(byte alpha)
    {
        var snapshot = RequireTarget();
        if (!snapshot.OpacitySupported)
            throw new InvalidOperationException("此窗口使用独立绘制表面，无法安全调节不透明度。置顶和隐藏仍可使用。");
        var hwnd = (nint)snapshot.Identity.Handle;
        if (!snapshot.OpacityChanged && (ReadStyle(hwnd) & WsExLayered) != 0 &&
            !Native.GetLayeredWindowAttributes(hwnd, out _, out _, out _))
            throw new InvalidOperationException("此窗口已切换绘制表面，无法安全调节不透明度。");
        if (!_preferBackdrop) RecordAppearanceChange(snapshot, changesAlpha: true, changesClickThrough: false);
        else if (NativeSurfaceAlpha(hwnd) < alpha)
        {
            // Background blending can only reduce brightness. If the application supplies
            // less alpha than the *newly entered* value, raise its base once on this user action.
            RecordAppearanceChange(snapshot, changesAlpha: true, changesClickThrough: false);
            ApplyAppearance(snapshot, 255, snapshot.RequestedClickThrough);
        }
        var previous = snapshot.RequestedAlpha;
        snapshot.RequestedAlpha = alpha;
        try { ApplyManagedAppearance(snapshot); }
        catch
        {
            snapshot.RequestedAlpha = previous;
            throw;
        }
    }

    public void SetClickThrough(bool enabled)
    {
        var snapshot = RequireTarget();
        if (!CanClickThrough) throw new InvalidOperationException("此窗口的绘制方式不支持鼠标穿透。");
        RecordAppearanceChange(snapshot, changesAlpha: enabled && !snapshot.OriginallyLayered, changesClickThrough: true);
        var previous = snapshot.RequestedClickThrough;
        snapshot.RequestedClickThrough = enabled;
        try
        {
            ApplyManagedAppearance(snapshot, releasePassLayer: !enabled);
            // If click-through supplied the only reason for layering, give back the original
            // style now. Subsequent ticks should leave the target's own appearance alone.
            if (!enabled && !snapshot.OriginallyLayered && !snapshot.RequestedAlpha.HasValue)
                snapshot.RequestedClickThrough = null;
        }
        catch
        {
            snapshot.RequestedClickThrough = previous;
            throw;
        }
    }

    /// <summary>Tracks the bound presentation without repeatedly writing a target's contested alpha.</summary>
    public void EnforceAppearance()
    {
        var snapshot = _snapshot;
        if (snapshot == null || (!snapshot.RequestedAlpha.HasValue && !snapshot.RequestedClickThrough.HasValue)) return;
        var hwnd = (nint)snapshot.Identity.Handle;
        if (Native.GetProp(hwnd, snapshot.Marker) != (nint)1) { _backdrop?.Hide(); return; }
        if (!_preferBackdrop && snapshot.RequestedAlpha is byte requested && requested < 255
            && NativeSurfaceAlpha(hwnd) != requested)
        {
            if (_backdropFailure != null)
            {
                // This display/driver cannot build the independent layer. Keep normal
                // click-through enforcement, but do not bring back the alpha tug of war.
                ApplyAppearance(snapshot, null, snapshot.RequestedClickThrough, removeUnusedLayer: false);
                if (!_reportedBackdropFailure)
                {
                    _reportedBackdropFailure = true;
                    throw new InvalidOperationException("目标程序已重设不透明度，当前显示环境无法启用稳定透明图层。"
                        + _backdropFailure);
                }
                return;
            }
            _preferBackdrop = true;
        }
        ApplyManagedAppearance(snapshot);
    }

    private static byte NativeSurfaceAlpha(nint hwnd)
    {
        if ((ReadStyle(hwnd) & WsExLayered) == 0) return 255;
        return Native.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) && (flags & LwaAlpha) != 0
            ? alpha : (byte)255;
    }

    private void ApplyManagedAppearance(Snapshot snapshot, bool releasePassLayer = false)
    {
        if (!_preferBackdrop || snapshot.RequestedAlpha is not byte requested)
        {
            ApplyAppearance(snapshot, snapshot.RequestedAlpha, snapshot.RequestedClickThrough);
            return;
        }

        // At zero the old native alpha path also passed mouse input through the window.
        // Preserve the explicit click-through choice when the user raises opacity again.
        bool? pass = snapshot.RequestedClickThrough;
        if (requested == 0)
        {
            RecordAppearanceChange(snapshot, changesAlpha: !snapshot.OriginallyLayered, changesClickThrough: true);
            pass = true;
        }
        else if (_zeroPass) pass ??= snapshot.OriginallyTransparent;
        if (pass.HasValue)
            ApplyAppearance(snapshot, null, pass, removeUnusedLayer: _zeroPass || releasePassLayer);
        _zeroPass = requested == 0;
        if (requested == 255)
        {
            _backdrop?.Hide();
            return;
        }

        try
        {
            _backdrop ??= new BackdropOpacityLayer(snapshot.Target.Handle);
            byte nativeAlpha = NativeSurfaceAlpha(snapshot.Target.Handle);
            // Account for opacity the target already supplies, including application fades.
            byte weight = nativeAlpha == 0 ? (byte)255 : (byte)Math.Min(255, (requested * 255 + nativeAlpha / 2) / nativeAlpha);
            _backdrop.Track(weight, (ReadStyle(snapshot.Target.Handle) & WsExTopmost) != 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _backdrop?.Dispose(); _backdrop = null;
            _preferBackdrop = false;
            _backdropFailure = ex.Message;
            // Restore the previous native presentation option once. A fresh binding may
            // try the compositor again after the driver or desktop session changes.
            RecordAppearanceChange(snapshot, changesAlpha: true, changesClickThrough: false);
            bool? fallbackPass = snapshot.RequestedClickThrough ?? (_zeroPass ? snapshot.OriginallyTransparent : null);
            ApplyAppearance(snapshot, requested, fallbackPass);
            _zeroPass = false;
        }
    }

    private void RecordAppearanceChange(Snapshot snapshot, bool changesAlpha, bool changesClickThrough)
    {
        var oldAlphaDirty = snapshot.OpacityChanged;
        var oldClickDirty = snapshot.ClickThroughChanged;
        snapshot.OpacityChanged |= changesAlpha;
        snapshot.ClickThroughChanged |= changesClickThrough;
        if (oldAlphaDirty == snapshot.OpacityChanged && oldClickDirty == snapshot.ClickThroughChanged) return;
        try { Persist(); }
        catch
        {
            snapshot.OpacityChanged = oldAlphaDirty;
            snapshot.ClickThroughChanged = oldClickDirty;
            throw;
        }
    }

    private static void ApplyAppearance(Snapshot snapshot, byte? alpha, bool? pass, bool removeUnusedLayer = true)
    {
        var hwnd = (nint)snapshot.Identity.Handle;
        long style = ReadStyle(hwnd);
        long desiredStyle = style;
        if (pass == true || (alpha.HasValue && (alpha < 255 || snapshot.OriginallyLayered)))
            desiredStyle |= WsExLayered;
        else if (removeUnusedLayer && pass == false && !alpha.HasValue && !snapshot.OriginallyLayered && snapshot.OpacityChanged)
            desiredStyle &= ~WsExLayered;
        if (pass.HasValue)
        {
            bool keepTransparent = pass.Value || ((desiredStyle & WsExLayered) == 0 && snapshot.OriginallyTransparent);
            desiredStyle = keepTransparent ? desiredStyle | WsExTransparent : desiredStyle & ~WsExTransparent;
        }

        bool addingLayer = (style & WsExLayered) == 0 && (desiredStyle & WsExLayered) != 0;
        if (addingLayer && !snapshot.OpacitySupported)
            throw new InvalidOperationException("窗口的独立绘制表面发生变化，请释放后重新绑定。");
        bool applyAlpha = addingLayer;
        uint flags = snapshot.LayeredFlags | LwaAlpha;
        uint color = snapshot.ColorKey;
        byte wanted = alpha ?? ((snapshot.LayeredFlags & LwaAlpha) != 0 ? snapshot.Alpha : (byte)255);
        if (!addingLayer && alpha.HasValue && (desiredStyle & WsExLayered) != 0)
        {
            if (!Native.GetLayeredWindowAttributes(hwnd, out color, out byte actual, out flags))
                throw new InvalidOperationException("此窗口已切换绘制表面，无法安全调节不透明度。");
            applyAlpha = (flags & LwaAlpha) == 0 ? wanted < 255 : actual != wanted;
            flags |= LwaAlpha;
        }
        if (style == desiredStyle && !applyAlpha) return;
        if (!SnapshotAlive(snapshot)) throw new InvalidOperationException("目标窗口已关闭或被替换。");
        if (style != desiredStyle) WriteStyle(hwnd, desiredStyle);
        if (applyAlpha && !Native.SetLayeredWindowAttributes(hwnd, color, wanted, flags))
        {
            var error = Failure("调节不透明度");
            if (addingLayer && SnapshotAlive(snapshot)) WriteStyle(hwnd, style);
            throw error;
        }
    }

    public void SetHidden(bool hidden)
    {
        var snapshot = RequireTarget();
        if (IsHidden == hidden) return;
        if (hidden)
        {
            snapshot.HidePlacement = PlacementData.From(ReadPlacement(snapshot.Target.Handle));
            snapshot.VisibilityChanged = true;
            Persist(); // Save visibility/placement before issuing SW_HIDE.
            _backdrop?.Hide();
            SetVisible(snapshot, false);
        }
        else
        {
            SetVisible(snapshot, true);
            // A successful show completes a hide/show cycle; original visibility still wins on Release.
            snapshot.VisibilityChanged = !snapshot.OriginalVisible;
            Persist();
            if (_backdrop != null) ApplyManagedAppearance(snapshot);
        }
    }

    public void Release()
    {
        _backdrop?.Dispose(); _backdrop = null;
        _preferBackdrop = _zeroPass = _reportedBackdropFailure = false;
        _backdropFailure = null;
        if (_snapshot == null) return;
        var snapshot = _snapshot;
        if (!SnapshotAlive(snapshot))
        {
            _snapshot = null;
            Persist();
            return;
        }
        var errors = new List<string>();
        snapshot.RequestedAlpha = null;
        snapshot.RequestedClickThrough = null;
        void RestorePart(Action operation, Action complete)
        {
            try { RequireTarget(); operation(); complete(); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        // Reveal first, so a failed style restore cannot leave an invisible application stranded.
        if (snapshot.VisibilityChanged)
            RestorePart(() => SetVisible(snapshot, snapshot.OriginalVisible), () => snapshot.VisibilityChanged = false);
        if (snapshot.OpacityChanged)
            RestorePart(() => RestoreOpacity(snapshot), () => snapshot.OpacityChanged = false);
        if (snapshot.ClickThroughChanged)
            RestorePart(() =>
            {
                long style = ReadStyle(snapshot.Target.Handle);
                long restored = snapshot.OriginallyTransparent ? style | WsExTransparent : style & ~WsExTransparent;
                if (restored != style) WriteStyle(snapshot.Target.Handle, restored);
            }, () => snapshot.ClickThroughChanged = false);
        if (snapshot.TopmostChanged)
            RestorePart(() => SetTopmostNative(snapshot.Target, snapshot.OriginalTopmost), () => snapshot.TopmostChanged = false);
        if (errors.Count == 0)
        {
            Native.RemoveProp(snapshot.Target.Handle, snapshot.Marker);
            _snapshot = null;
        }
        Persist();
        if (errors.Count > 0) throw new InvalidOperationException("窗口未完全恢复：\n" + string.Join("\n", errors));
    }

    public void RecoverPreviousSession()
    {
        if (_snapshot != null || !File.Exists(_journalPath)) return;
        Snapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_journalPath), JsonOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("无法读取上次的窗口恢复记录：" + ex.Message, ex); }
        if (snapshot?.Identity != null && snapshot.Marker.StartsWith("WindowPilot.Managed.", StringComparison.Ordinal)
            && SnapshotAlive(snapshot)) _snapshot = snapshot;
        Release();
        if (_snapshot == null) Persist();
    }

    public void Dispose() => Release();

    private Snapshot RequireTarget()
    {
        if (_snapshot == null) throw new InvalidOperationException("请先绑定一个窗口。");
        if (!SnapshotAlive(_snapshot)) throw new InvalidOperationException("绑定的窗口已关闭或被替换，请重新选择窗口。");
        return _snapshot;
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_journalPath))!);
            var temporary = _journalPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _snapshot, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, _journalPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("无法保存窗口恢复记录，已停止本次操作：" + ex.Message, ex); }
    }

    private static void SetTopmostNative(WindowTarget target, bool enabled)
    {
        const uint flags = 0x1 | 0x2 | 0x10 | 0x200 | 0x4000; // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER|ASYNC
        if (!Native.SetWindowPos(target.Handle, enabled ? (nint)(-1) : (nint)(-2), 0, 0, 0, 0, flags))
            throw Failure(enabled ? "设置置顶" : "取消置顶");
        if (!WaitFor(() => !Native.IsWindow(target.Handle) || ((ReadStyle(target.Handle) & WsExTopmost) != 0) == enabled))
            throw new InvalidOperationException("目标窗口暂未响应置顶操作，请稍后重试。");
        if (!Native.IsWindow(target.Handle)) throw new InvalidOperationException("目标窗口已关闭。");
    }

    private static void SetVisible(Snapshot snapshot, bool visible)
    {
        var hwnd = snapshot.Target.Handle;
        // SW_HIDE removes an ordinary running taskbar button. A pinned launcher is a separate shell item.
        // Never call ITaskbarList.AddTab: owned/tool windows may never have had their own button.
        if (!visible)
        {
            if (!Native.ShowWindowAsync(hwnd, 0)) throw Failure("隐藏窗口");
        }
        else
        {
            var before = snapshot.HidePlacement.ToNative();
            // Hide itself preserves max/min state and geometry. Only restore placement if the target moved while hidden.
            var now = ReadPlacement(hwnd);
            if (!SamePlacement(now, before))
            {
                before.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT prevents a hung foreign UI thread blocking this caller.
                if (!Native.SetWindowPlacement(hwnd, ref before)) throw Failure("恢复窗口位置");
            }
            if (!Native.ShowWindowAsync(hwnd, before.ShowCommand is 2 or 6 or 7 ? 7 : 8))
                throw Failure("恢复显示窗口"); // SHOWMINNOACTIVE / SHOWNA
        }
        // Async success means queued, so verify the state within a bounded interval.
        if (!WaitFor(() => !Native.IsWindow(hwnd) || Native.IsWindowVisible(hwnd) == visible))
            throw new InvalidOperationException("窗口暂未响应显示操作，恢复记录已保留。请稍后重试。");
        if (!Native.IsWindow(hwnd)) throw new InvalidOperationException("目标窗口已关闭。");
    }

    private static bool SamePlacement(WindowPlacement a, WindowPlacement b) => a.ShowCommand == b.ShowCommand
        && a.NormalPosition.Left == b.NormalPosition.Left && a.NormalPosition.Top == b.NormalPosition.Top
        && a.NormalPosition.Right == b.NormalPosition.Right && a.NormalPosition.Bottom == b.NormalPosition.Bottom;

    private static void RestoreOpacity(Snapshot snapshot)
    {
        var hwnd = snapshot.Target.Handle;
        if (snapshot.OriginallyLayered)
        {
            if ((ReadStyle(hwnd) & WsExLayered) == 0) WriteStyle(hwnd, ReadStyle(hwnd) | WsExLayered);
            if (!Native.SetLayeredWindowAttributes(hwnd, snapshot.ColorKey, snapshot.Alpha, snapshot.LayeredFlags))
                throw Failure("恢复原始不透明度");
        }
        else
        {
            WriteStyle(hwnd, ReadStyle(hwnd) & ~WsExLayered);
            Native.RedrawWindow(hwnd, 0, 0, 0x1 | 0x4 | 0x80 | 0x400);
        }
    }

    private static bool SnapshotAlive(Snapshot snapshot) => IdentityAlive(snapshot.Target)
        && Native.GetProp(snapshot.Target.Handle, snapshot.Marker) == (nint)1;

    private static bool IdentityAlive(WindowTarget target)
    {
        if (!Native.IsWindow(target.Handle)) return false;
        var thread = Native.GetWindowThreadProcessId(target.Handle, out var pid);
        if (thread != target.ThreadId || pid != target.ProcessId || ReadClass(target.Handle) != target.ClassName) return false;
        if (target.ProcessStartTicks != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.StartTime.ToUniversalTime().Ticks != target.ProcessStartTicks) return false;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
        }
        return true;
    }

    private static bool SameIdentity(WindowTarget a, WindowTarget b) => a.Handle == b.Handle && a.ProcessId == b.ProcessId
        && a.ThreadId == b.ThreadId && a.ClassName == b.ClassName && a.ProcessStartTicks == b.ProcessStartTicks;

    private static WindowTarget? ReadTarget(nint hwnd, Dictionary<uint, (string Name, long Started)>? cache)
    {
        if (hwnd == 0 || !Native.IsWindowVisible(hwnd) || hwnd == Native.GetShellWindow()) return null;
        var thread = Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId || thread == 0) return null;
        var className = ReadClass(hwnd);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        if ((ReadStyle(hwnd) & WsExToolWindow) != 0) return null;
        if (Native.DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        var buffer = new StringBuilder(2048);
        Native.GetWindowText(hwnd, buffer, buffer.Capacity);
        var title = buffer.ToString().Trim();
        if (title.Length == 0) return null;
        (string Name, long Started) info;
        if (cache == null || !cache.TryGetValue(pid, out info))
        {
            info = ($"进程 {pid}", 0);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                info.Name = process.ProcessName;
                try { info.Started = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { }
            if (cache != null) cache[pid] = info;
        }
        return new WindowTarget(hwnd, pid, thread, className, title, info.Name, info.Started);
    }

    private static WindowPlacement ReadPlacement(nint hwnd)
    {
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!Native.GetWindowPlacement(hwnd, ref placement)) throw Failure("读取窗口位置");
        return placement;
    }

    private static string ReadClass(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        Native.GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static long ReadStyle(nint hwnd)
    {
        Marshal.SetLastPInvokeError(0);
        var result = nint.Size == 8 ? Native.GetWindowLongPtr(hwnd, GwlExStyle) : (nint)Native.GetWindowLong(hwnd, GwlExStyle);
        var error = Marshal.GetLastPInvokeError();
        if (result == 0 && error != 0) throw Failure("读取窗口样式", error);
        return result.ToInt64();
    }

    private static ulong GetClassStyle(nint hwnd) => nint.Size == 8
        ? Native.GetClassLongPtr(hwnd, -26).ToUInt64() : Native.GetClassLong(hwnd, -26);

    private static void WriteStyle(nint hwnd, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var result = nint.Size == 8 ? Native.SetWindowLongPtr(hwnd, GwlExStyle, (nint)style)
            : (nint)Native.SetWindowLong(hwnd, GwlExStyle, unchecked((int)style));
        var error = Marshal.GetLastPInvokeError();
        if (result == 0 && error != 0) throw Failure("调整窗口样式", error);
    }

    private static bool WaitFor(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        do { if (predicate()) return true; Thread.Sleep(10); } while (watch.ElapsedMilliseconds < 700);
        return predicate();
    }

    private static Exception Failure(string operation, int? errorCode = null)
    {
        var error = errorCode ?? Marshal.GetLastPInvokeError();
        var detail = error == 0 ? "目标窗口可能已关闭或暂时无响应。" : new Win32Exception(error).Message;
        if (error == 5) detail += " 目标程序可能以管理员身份运行；请以相同权限启动本工具。";
        return new InvalidOperationException($"{operation}失败（Windows {error}）：{detail}");
    }

    private sealed class Snapshot
    {
        public Identity Identity { get; set; } = null!;
        [JsonIgnore] public WindowTarget Target => Identity.ToTarget();
        public string Marker { get; set; } = "";
        public bool OriginalTopmost { get; set; }
        public bool TopmostChanged { get; set; }
        public bool OriginallyLayered { get; set; }
        public bool OriginallyTransparent { get; set; }
        public bool ClickThroughChanged { get; set; }
        [JsonIgnore] public bool? RequestedClickThrough { get; set; }
        [JsonIgnore] public byte? RequestedAlpha { get; set; }
        public bool OpacitySupported { get; set; }
        public uint ColorKey { get; set; }
        public byte Alpha { get; set; } = 255;
        public uint LayeredFlags { get; set; } = LwaAlpha;
        public bool OpacityChanged { get; set; }
        public bool OriginalVisible { get; set; }
        public bool VisibilityChanged { get; set; }
        public PlacementData HidePlacement { get; set; } = new();
    }

    private sealed class Identity
    {
        public long Handle { get; set; }
        public uint ProcessId { get; set; }
        public uint ThreadId { get; set; }
        public string ClassName { get; set; } = "";
        public string Title { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public long ProcessStartTicks { get; set; }
        public WindowTarget ToTarget() => new((nint)Handle, ProcessId, ThreadId, ClassName, Title, ProcessName, ProcessStartTicks);
        public static Identity From(WindowTarget x) => new() { Handle = x.Handle.ToInt64(), ProcessId = x.ProcessId,
            ThreadId = x.ThreadId, ClassName = x.ClassName, Title = x.Title, ProcessName = x.ProcessName, ProcessStartTicks = x.ProcessStartTicks };
    }

    private sealed class PlacementData
    {
        public uint Flags { get; set; }
        public uint ShowCommand { get; set; }
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public static PlacementData From(WindowPlacement x) => new() { Flags = x.Flags, ShowCommand = x.ShowCommand,
            MinX = x.MinPosition.X, MinY = x.MinPosition.Y, MaxX = x.MaxPosition.X, MaxY = x.MaxPosition.Y,
            Left = x.NormalPosition.Left, Top = x.NormalPosition.Top, Right = x.NormalPosition.Right, Bottom = x.NormalPosition.Bottom };
        public WindowPlacement ToNative() => new() { Length = (uint)Marshal.SizeOf<WindowPlacement>(), Flags = Flags,
            ShowCommand = ShowCommand, MinPosition = new Point { X = MinX, Y = MinY }, MaxPosition = new Point { X = MaxX, Y = MaxY },
            NormalPosition = new Rect { Left = Left, Top = Top, Right = Right, Bottom = Bottom } };
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    { public uint Length, Flags, ShowCommand; public Point MinPosition, MaxPosition; public Rect NormalPosition; }

    private static class Native
    {
        internal delegate bool EnumWindowsProc(nint hwnd, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetShellWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int count);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] internal static extern int SetWindowLong(nint hwnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] internal static extern nuint GetClassLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] internal static extern uint GetClassLong(nint hwnd, int index);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLayeredWindowAttributes(nint hwnd, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint hwnd, int command);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPlacement(nint hwnd, ref WindowPlacement placement);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProp(nint hwnd, string name, nint value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint hwnd, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint RemoveProp(nint hwnd, string name);
    }
}
