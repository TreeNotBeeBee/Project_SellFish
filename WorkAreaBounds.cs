using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WindowPilot;

internal static class WorkAreaBounds
{
    private const uint NearestMonitor = 2; // MONITOR_DEFAULTTONEAREST

    internal static RectInt32? ForWindow(nint hwnd) => Read(MonitorFromWindow(hwnd, NearestMonitor));

    internal static RectInt32? ForPoint(PointInt32 point) => Read(MonitorFromPoint(point, NearestMonitor));

    private static RectInt32? Read(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return null;
        // rcWork 已是整个虚拟桌面的物理坐标；副屏在主屏左侧时必须保留负值。
        var work = info.Work;
        var area = FromEdges(work.Left, work.Top, work.Right, work.Bottom);
        return area.Width > 0 && area.Height > 0 ? area : null;
    }

    internal static RectInt32 FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    internal static PointInt32 Clamp(PointInt32 position, SizeInt32 size, RectInt32 area) => new(
        Math.Clamp(position.X, area.X, area.X + Math.Max(0, area.Width - size.Width)),
        Math.Clamp(position.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height)));

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Screen, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(PointInt32 point, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
