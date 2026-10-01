using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AetherialEviction;

internal sealed class WindowPlacementService
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorInfoPrimary = 0x00000001;

    public string Status { get; private set; } = "Window position idle.";

    public void SetStatus(string status)
        => Status = status;

    public bool TryReadCurrent(out WindowPlacementSnapshot snapshot)
    {
        snapshot = default;
        if (!TryResolveWindowHandle(out var windowHandle))
        {
            Status = "FFXIV window handle is not available yet.";
            return false;
        }

        if (!GetWindowRect(windowHandle, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            Status = LastWin32Error("GetWindowRect");
            return false;
        }

        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == nint.Zero || !TryReadMonitor(monitorHandle, out var monitor))
        {
            Status = "The monitor containing the FFXIV window could not be resolved.";
            return false;
        }

        snapshot = new WindowPlacementSnapshot(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            monitor.DeviceName,
            monitor.Left,
            monitor.Top,
            monitor.Right,
            monitor.Bottom);
        Status = "Current FFXIV window position read.";
        return true;
    }

    public bool TrySave(out SavedWindowPlacement? placement)
    {
        placement = null;
        if (!TryReadCurrent(out var current))
            return false;

        placement = new SavedWindowPlacement
        {
            X = current.X,
            Y = current.Y,
            Width = current.Width,
            Height = current.Height,
            MonitorDeviceName = current.MonitorDeviceName,
            MonitorLeft = current.MonitorLeft,
            MonitorTop = current.MonitorTop,
            MonitorRight = current.MonitorRight,
            MonitorBottom = current.MonitorBottom,
            SavedUtc = DateTime.UtcNow,
        };
        Status = $"Saved FFXIV window at {placement.X},{placement.Y} ({placement.Width}x{placement.Height}).";
        return true;
    }

    public bool TryMove(int x, int y)
    {
        if (!TryResolveWindowHandle(out var windowHandle))
            return false;

        if (!SetWindowPos(windowHandle, nint.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
        {
            Status = LastWin32Error("SetWindowPos");
            return false;
        }

        Status = $"Moved FFXIV window to {x},{y}.";
        return true;
    }

    public bool TryResize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            Status = "Window width and height must be positive.";
            return false;
        }

        if (!TryResolveWindowHandle(out var windowHandle))
            return false;

        if (!SetWindowPos(windowHandle, nint.Zero, 0, 0, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate))
        {
            Status = LastWin32Error("SetWindowPos");
            return false;
        }

        Status = $"Resized FFXIV window to {width}x{height}.";
        return true;
    }

    public bool TryRestorePosition(SavedWindowPlacement placement)
    {
        if (!TryResolveWindowHandle(out var windowHandle))
            return false;

        var monitor = FindSavedMonitor(placement.MonitorDeviceName);
        if (monitor is null)
        {
            Status = "Saved monitor is unavailable; position was not restored.";
            return false;
        }

        var targetX = monitor.Value.Left + placement.X - placement.MonitorLeft;
        var targetY = monitor.Value.Top + placement.Y - placement.MonitorTop;
        var x = Math.Clamp(targetX, monitor.Value.Left, Math.Max(monitor.Value.Left, monitor.Value.Right - 1));
        var y = Math.Clamp(targetY, monitor.Value.Top, Math.Max(monitor.Value.Top, monitor.Value.Bottom - 1));

        if (!SetWindowPos(windowHandle, nint.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
        {
            Status = LastWin32Error("SetWindowPos");
            return false;
        }

        Status = $"Restored FFXIV window position to {x},{y} on {monitor.Value.DeviceName}.";
        return true;
    }

    public bool TryRestoreSize(SavedWindowPlacement placement)
    {
        if (placement.Width <= 0 || placement.Height <= 0)
        {
            Status = "Saved window size is unavailable.";
            return false;
        }

        return TryResize(placement.Width, placement.Height);
    }

    private static bool TryResolveWindowHandle(out nint windowHandle)
    {
        windowHandle = nint.Zero;
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        if (IsUsableWindow(process.MainWindowHandle))
        {
            windowHandle = process.MainWindowHandle;
            return true;
        }

        var processId = (uint)process.Id;
        var found = nint.Zero;
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var candidateProcessId);
            if (candidateProcessId != processId || !IsUsableWindow(candidate))
                return true;

            found = candidate;
            return false;
        }, nint.Zero);
        windowHandle = found;
        return windowHandle != nint.Zero;
    }

    private static MonitorSnapshot? FindSavedMonitor(string? deviceName)
    {
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            foreach (var monitor in monitors)
            {
                if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return monitor;
            }
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary);
    }

    private static List<MonitorSnapshot> EnumerateMonitors()
    {
        var monitors = new List<MonitorSnapshot>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (nint handle, nint _, ref Rect monitorRect, nint __) =>
        {
            if (TryReadMonitor(handle, out var monitor))
                monitors.Add(monitor);
            return true;
        }, nint.Zero);
        return monitors;
    }

    private static bool TryReadMonitor(nint handle, out MonitorSnapshot monitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(handle, ref info))
        {
            monitor = default;
            return false;
        }

        monitor = new MonitorSnapshot(
            info.DeviceName,
            (info.Flags & MonitorInfoPrimary) != 0,
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right,
            info.Monitor.Bottom);
        return true;
    }

    private static bool IsUsableWindow(nint handle)
        => handle != nint.Zero
        && IsWindow(handle)
        && IsWindowVisible(handle)
        && GetWindowRect(handle, out var rect)
        && rect.Right > rect.Left
        && rect.Bottom > rect.Top;

    private static string LastWin32Error(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return $"{operation} failed (Win32 {error}: {new Win32Exception(error).Message}).";
    }

    private readonly record struct MonitorSnapshot(
        string DeviceName,
        bool IsPrimary,
        int Left,
        int Top,
        int Right,
        int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool EnumWindowsProc(nint windowHandle, nint parameter);
    private delegate bool MonitorEnumProc(nint monitorHandle, nint deviceContext, ref Rect monitorRect, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint deviceContext, nint clipRect, MonitorEnumProc callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint windowHandle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}

internal readonly record struct WindowPlacementSnapshot(
    int X,
    int Y,
    int Width,
    int Height,
    string MonitorDeviceName,
    int MonitorLeft,
    int MonitorTop,
    int MonitorRight,
    int MonitorBottom);
