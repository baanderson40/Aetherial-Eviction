using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using ECommons.GameFunctions;

namespace AetherialEviction;

internal sealed class BackgroundRenderGateService : IDisposable
{
    private bool _enabled;
    private bool _onlyWhenMinimized;
    private bool _pauseDuringCombat;
    private int _safetyFrameIntervalMs = 5_000;
    private long _nextSafetyRenderTick;
    private bool _renderRequestActive;
    private bool _disposed;
    private DateTime _dutyCompletionBypassUntilUtc = DateTime.MinValue;

    public bool IsRenderSuppressed => _renderRequestActive;
    public bool RenderRequestActive => _renderRequestActive;
    public string Status { get; private set; } = "Background no-render disabled.";
    public bool TransitionBypassActive { get; private set; }
    public bool DutyCompletionBypassActive => DateTime.UtcNow < _dutyCompletionBypassUntilUtc;
    public bool CombatRenderBypassActive => _pauseDuringCombat && Plugin.Condition[ConditionFlag.InCombat];
    public int SafetyFrameIntervalSeconds => _safetyFrameIntervalMs / 1_000;

    public void RefreshState(Configuration configuration)
    {
        _enabled = configuration.BackgroundNoRenderEnabled;
        _onlyWhenMinimized = configuration.BackgroundNoRenderOnlyWhenMinimized;
        _pauseDuringCombat = configuration.BackgroundNoRenderPauseDuringCombat;
        _safetyFrameIntervalMs = Math.Clamp(configuration.BackgroundSafetyFrameIntervalSeconds, 1, 60) * 1_000;
        _nextSafetyRenderTick = 0;

        Status = _enabled
            ? _onlyWhenMinimized
                ? "Background no-render armed. Minimize the client to activate it."
                : "Background no-render armed. Switch to another window to activate it."
            : "Background no-render disabled.";
    }

    public void Update()
    {
        if (_disposed)
            return;

        TransitionBypassActive = IsAreaTransitionActive();
        if (!_enabled || !Plugin.ClientState.IsLoggedIn)
        {
            RemoveRenderRequest();
            return;
        }

        if (TransitionBypassActive)
        {
            RemoveRenderRequest();
            Status = "Area-transition render bypass active.";
            return;
        }

        if (DutyCompletionBypassActive)
        {
            RemoveRenderRequest();
            Status = "Duty completion render bypass active.";
            return;
        }

        if (CombatRenderBypassActive)
        {
            RemoveRenderRequest();
            Status = "Combat render bypass active.";
            return;
        }

        if (!ShouldSuppressRender())
        {
            RemoveRenderRequest();
            return;
        }

        var currentTick = Environment.TickCount64;
        if (_nextSafetyRenderTick - currentTick < 0)
        {
            _nextSafetyRenderTick = currentTick + _safetyFrameIntervalMs;
            RemoveRenderRequest();
            Status = "Background no-render safety frame active.";
            return;
        }

        PlaceRenderRequest();
    }

    public void StartDutyCompletionBypass(TimeSpan duration)
    {
        _dutyCompletionBypassUntilUtc = DateTime.UtcNow + duration;
        Status = $"Duty completion render bypass active for {duration.TotalSeconds:0}s.";
        Plugin.Log.Information("[Aetherial Eviction] Duty completion render bypass started for {Seconds:0}s.", duration.TotalSeconds);
    }

    public void ClearDutyCompletionBypass()
    {
        _dutyCompletionBypassUntilUtc = DateTime.MinValue;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_renderRequestActive)
            return;

        if (Plugin.Framework.IsInFrameworkUpdateThread)
            RemoveRenderRequest();
        else
            _ = Plugin.Framework.RunOnFrameworkThread(RemoveRenderRequest);
    }

    private void PlaceRenderRequest()
    {
        if (_renderRequestActive)
            return;

        RenderDisableManager.PlaceRequest();
        _renderRequestActive = true;
        Status = "Background no-render active. Safety frames are still allowed.";
        Plugin.Log.Information("[Aetherial Eviction] Background no-render activated.");
    }

    private void RemoveRenderRequest()
    {
        if (!_renderRequestActive)
            return;

        RenderDisableManager.RemoveRequest();
        _renderRequestActive = false;
        Status = _enabled
            ? _onlyWhenMinimized
                ? "Background no-render armed. Minimize the client to activate it."
                : "Background no-render armed. Switch to another window to activate it."
            : "Background no-render disabled.";
        Plugin.Log.Information("[Aetherial Eviction] Background no-render deactivated.");
    }

    private bool ShouldSuppressRender()
    {
        if (!TryResolveWindowHandle(out var windowHandle))
            return false;

        if (_onlyWhenMinimized)
            return IsIconic(windowHandle);

        var foregroundWindow = GetForegroundWindow();
        return foregroundWindow != nint.Zero && foregroundWindow != windowHandle;
    }

    private static bool TryResolveWindowHandle(out nint windowHandle)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        windowHandle = process.MainWindowHandle;
        return windowHandle != nint.Zero;
    }

    private static bool IsAreaTransitionActive()
        => Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51];

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);
}
