using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;

namespace AetherialEviction;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/aeviction";
    private const string CommandAlias = "/ae";
    private const long BytesPerGigabyte = 1024L * 1024L * 1024L;
    private const double CheckIntervalSeconds = 1.0d;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static ICondition Condition { get; private set; } = null!;

    [PluginService]
    internal static IDutyState DutyState { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem _windowSystem = new("AetherialEviction");
    private readonly Configuration _configuration;
    private readonly ConfigWindow _configWindow;
    private readonly BackgroundRenderGateService _backgroundRenderGateService;
    private readonly WindowPlacementService _windowPlacementService;
    private DateTime _nextCheckUtc = DateTime.MinValue;
    private DateTime _nextWindowAutoLoadAttemptUtc = DateTime.MinValue;
    private DateTime _lastTrimUtc = DateTime.MinValue;
    private double _lastTrimBeforeGb;
    private double _lastTrimAfterGb;
    private string _status = "Automatic trimming is disabled.";
    private bool _windowAutoLoadAttempted;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        _configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _configuration.Initialize(PluginInterface);
        MigrateConfiguration();

        _backgroundRenderGateService = new BackgroundRenderGateService();
        _windowPlacementService = new WindowPlacementService();
        _configWindow = new ConfigWindow(this, _configuration);
        _windowSystem.AddWindow(_configWindow);

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Aetherial Eviction settings."
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Aetherial Eviction settings."
        });
        Framework.Update += OnFrameworkUpdate;
        DutyState.DutyStarted += OnDutyStarted;
        DutyState.DutyCompleted += OnDutyCompleted;
        ApplyConfiguration();

        Log.Information("[Aetherial Eviction] Loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        DutyState.DutyStarted -= OnDutyStarted;
        DutyState.DutyCompleted -= OnDutyCompleted;
        _backgroundRenderGateService.Dispose();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        _windowSystem.RemoveAllWindows();
        ECommonsMain.Dispose();
    }

    internal double CurrentWorkingSetGb
    {
        get
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.WorkingSet64 / (double)BytesPerGigabyte;
            }
            catch
            {
                return 0.0d;
            }
        }
    }

    internal DateTime LastTrimUtc => _lastTrimUtc;
    internal double LastTrimBeforeGb => _lastTrimBeforeGb;
    internal double LastTrimAfterGb => _lastTrimAfterGb;
    internal string Status => _status;
    internal Configuration Configuration => _configuration;
    internal BackgroundRenderGateService BackgroundRenderGateService => _backgroundRenderGateService;
    internal WindowPlacementService WindowPlacementService => _windowPlacementService;

    private void OnCommand(string command, string arguments)
    {
        var parts = (arguments ?? string.Empty).Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            OpenConfigUi();
            return;
        }

        var feature = parts[0].ToLowerInvariant();
        if (feature is "help" or "?")
        {
            PrintCommandHelp();
            return;
        }

        if (feature is "status")
        {
            PrintStatus();
            return;
        }

        if (parts.Length < 2)
        {
            PrintChat($"Missing action for '{feature}'. Use /ae help.");
            return;
        }

        var action = parts[1].ToLowerInvariant();
        switch (feature)
        {
            case "memory":
                SetMemoryEnabled(action);
                break;
            case "background":
            case "render":
                SetBackgroundEnabled(action);
                break;
            case "window":
            case "position":
                HandleWindowCommand(action);
                break;
            default:
                PrintChat($"Unknown feature '{feature}'. Use /ae help.");
                break;
        }
    }

    private void SetMemoryEnabled(string action)
    {
        if (!TryResolveToggle(action, _configuration.Enabled, out var enabled))
        {
            PrintChat("Usage: /ae memory enable|disable|toggle");
            return;
        }

        _configuration.Enabled = enabled;
        _configuration.Save();
        PrintChat($"Memory trimming {(enabled ? "enabled" : "disabled")}.");
    }

    private void SetBackgroundEnabled(string action)
    {
        if (!TryResolveToggle(action, _configuration.BackgroundNoRenderEnabled, out var enabled))
        {
            PrintChat("Usage: /ae background enable|disable|toggle");
            return;
        }

        _configuration.BackgroundNoRenderEnabled = enabled;
        _configuration.Save();
        ApplyConfiguration();
        PrintChat($"Background no-render {(enabled ? "enabled" : "disabled")}.");
    }

    private void HandleWindowCommand(string action)
    {
        switch (action)
        {
            case "enable":
            case "disable":
            case "toggle":
                if (!TryResolveToggle(action, _configuration.WindowPlacementAutoLoadEnabled, out var enabled))
                    return;

                _configuration.WindowPlacementAutoLoadEnabled = enabled;
                _configuration.WindowSizeAutoLoadEnabled = enabled;
                _configuration.Save();
                PrintChat($"Window position and size auto-load {(enabled ? "enabled" : "disabled")}.");
                return;
            case "load":
                PrintChat(FormatCommandResult(LoadSavedWindow(includeSize: true),
                    "Window position and size restored.", "Window restore failed."));
                return;
            case "save":
                PrintChat(FormatCommandResult(SaveCurrentWindowPlacement(),
                    "Current window position and size saved.", "Window save failed."));
                return;
            case "reset":
                ResetWindowPlacement();
                PrintChat("Saved window position and size cleared; auto-load disabled.");
                return;
            default:
                PrintChat("Usage: /ae window enable|disable|toggle|load|save|reset");
                return;
        }
    }

    private static string FormatCommandResult(bool success, string successMessage, string failureMessage)
        => success ? successMessage : failureMessage;

    private static bool TryResolveToggle(string action, bool current, out bool enabled)
    {
        enabled = current;
        switch (action)
        {
            case "enable":
                enabled = true;
                return true;
            case "disable":
                enabled = false;
                return true;
            case "toggle":
                enabled = !current;
                return true;
            default:
                return false;
        }
    }

    private void PrintCommandHelp()
    {
        PrintChat("/ae [memory|background|window] enable|disable|toggle");
        PrintChat("/ae window load|save|reset | /ae status | /ae help");
    }

    private void PrintStatus()
    {
        PrintChat($"Memory: {(_configuration.Enabled ? "enabled" : "disabled")}; working set: {CurrentWorkingSetGb:0.00} GB.");
        PrintChat($"Background: {(_configuration.BackgroundNoRenderEnabled ? "enabled" : "disabled")}; combat bypass: {(_configuration.BackgroundNoRenderPauseDuringCombat ? "on" : "off")}; {BackgroundRenderGateService.Status}");
        PrintChat($"Window auto-load: {(_configuration.WindowPlacementAutoLoadEnabled ? "enabled" : "disabled")}; {_windowPlacementService.Status}");
    }

    private static void PrintChat(string message)
    {
        ChatGui.Print(message, "Aetherial Eviction");
    }

    private void OpenConfigUi()
    {
        _configWindow.IsOpen = true;
    }

    private void DrawUi()
    {
        _windowSystem.Draw();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        _backgroundRenderGateService.Update();

        var now = DateTime.UtcNow;
        TickWindowAutoLoad(now);
        if (now < _nextCheckUtc)
            return;

        _nextCheckUtc = now.AddSeconds(CheckIntervalSeconds);
        if (!_configuration.Enabled)
        {
            _status = "Automatic trimming is disabled.";
            return;
        }

        if (_configuration.SafetyProtection && TryGetSafetyBlockReason(out var reason))
        {
            _status = $"Deferred: {reason}";
            return;
        }

        double workingSetGb;
        var thresholdGb = Math.Clamp(_configuration.ThresholdGb, 0.25f, 64.0f);
        var cooldownSeconds = Math.Clamp(_configuration.CooldownSeconds, 1.0f, 3600.0f);
        try
        {
            using var process = Process.GetCurrentProcess();
            workingSetGb = process.WorkingSet64 / (double)BytesPerGigabyte;

            if (workingSetGb < thresholdGb)
            {
                _status = $"Below threshold ({workingSetGb:0.00} / {thresholdGb:0.00} GB).";
                return;
            }

            if (_lastTrimUtc != DateTime.MinValue &&
                (now - _lastTrimUtc).TotalSeconds < cooldownSeconds)
            {
                var remaining = cooldownSeconds - (now - _lastTrimUtc).TotalSeconds;
                _status = $"Cooldown: {Math.Max(0.0d, remaining):0}s remaining.";
                return;
            }

            if (!NativeMethods.EmptyWorkingSet(process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                _status = $"Trim failed (Win32 error {error}).";
                Log.Warning("[Aetherial Eviction] EmptyWorkingSet failed for PID {ProcessId}; Win32 error {Error}.",
                    process.Id, error);
                return;
            }

            _lastTrimUtc = now;
            _lastTrimBeforeGb = workingSetGb;
            process.Refresh();
            _lastTrimAfterGb = process.WorkingSet64 / (double)BytesPerGigabyte;
            _status = $"Trimmed {_lastTrimBeforeGb:0.00} -> {_lastTrimAfterGb:0.00} GB.";
            Log.Information("[Aetherial Eviction] Trimmed PID {ProcessId}: {Before:0.00} -> {After:0.00} GB.",
                process.Id, _lastTrimBeforeGb, _lastTrimAfterGb);
        }
        catch (Exception ex)
        {
            _status = "Trim failed; see plugin log.";
            Log.Error(ex, "[Aetherial Eviction] Could not inspect or trim the current process.");
        }
    }

    internal void ApplyConfiguration()
    {
        _backgroundRenderGateService.RefreshState(_configuration);
    }

    private void OnDutyStarted(IDutyStateEventArgs _)
    {
        _backgroundRenderGateService.ClearDutyCompletionBypass();
    }

    private void OnDutyCompleted(IDutyStateEventArgs _)
    {
        var seconds = Math.Clamp(_configuration.BackgroundDutyCompletionBypassSeconds, 1, 120);
        _backgroundRenderGateService.StartDutyCompletionBypass(TimeSpan.FromSeconds(seconds));
    }

    private void MigrateConfiguration()
    {
        if (_configuration.Version >= 1)
            return;

        _configuration.Version = 1;
        _configuration.Save();
    }

    private void TickWindowAutoLoad(DateTime now)
    {
        if (_windowAutoLoadAttempted || !_configuration.WindowPlacementAutoLoadEnabled && !_configuration.WindowSizeAutoLoadEnabled)
            return;

        if (!ClientState.IsLoggedIn || now < _nextWindowAutoLoadAttemptUtc)
            return;

        var placement = _configuration.WindowPlacement;
        if (placement == null)
        {
            _windowAutoLoadAttempted = true;
            _windowPlacementService.SetStatus("Window auto-load enabled, but no saved placement exists.");
            return;
        }

        var succeeded = true;
        if (_configuration.WindowPlacementAutoLoadEnabled)
            succeeded = _windowPlacementService.TryRestorePosition(placement) && succeeded;
        if (_configuration.WindowSizeAutoLoadEnabled)
            succeeded = _windowPlacementService.TryRestoreSize(placement) && succeeded;

        if (succeeded)
        {
            _windowAutoLoadAttempted = true;
            return;
        }

        _nextWindowAutoLoadAttemptUtc = now.AddSeconds(5.0d);
    }

    internal bool SaveCurrentWindowPlacement()
    {
        if (!_windowPlacementService.TrySave(out var placement) || placement == null)
            return false;

        _configuration.WindowPlacement = placement;
        _configuration.Save();
        return true;
    }

    internal bool MoveGameWindow(int x, int y)
        => _windowPlacementService.TryMove(x, y);

    internal bool ResizeGameWindow(int width, int height)
        => _windowPlacementService.TryResize(width, height);

    internal bool LoadSavedWindow(bool includeSize)
    {
        var placement = _configuration.WindowPlacement;
        if (placement == null)
        {
            _windowPlacementService.SetStatus("No saved window placement exists.");
            return false;
        }

        var result = _windowPlacementService.TryRestorePosition(placement);
        if (includeSize)
            result = _windowPlacementService.TryRestoreSize(placement) && result;
        return result;
    }

    internal void ResetWindowPlacement()
    {
        _configuration.WindowPlacement = null;
        _configuration.WindowPlacementAutoLoadEnabled = false;
        _configuration.WindowSizeAutoLoadEnabled = false;
        _configuration.Save();
        _windowAutoLoadAttempted = false;
        _windowPlacementService.SetStatus("Saved window placement cleared.");
    }

    private bool TryGetSafetyBlockReason(out string reason)
    {
        reason = string.Empty;

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            reason = "foreground window is unknown";
            return true;
        }

        if (NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId) == 0)
        {
            reason = "foreground process is unknown";
            return true;
        }

        using var currentProcess = Process.GetCurrentProcess();
        if (foregroundProcessId == (uint)currentProcess.Id)
        {
            reason = "client is foreground";
            return true;
        }

        if (Condition[ConditionFlag.InCombat])
        {
            reason = "in combat";
            return true;
        }

        if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "loading or changing areas";
            return true;
        }

        if (Condition[ConditionFlag.Occupied] ||
            Condition[ConditionFlag.Occupied30] ||
            Condition[ConditionFlag.Occupied33] ||
            Condition[ConditionFlag.Occupied38] ||
            Condition[ConditionFlag.Occupied39] ||
            Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Condition[ConditionFlag.OccupiedInEvent] ||
            Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Condition[ConditionFlag.OccupiedSummoningBell] ||
            Condition[ConditionFlag.Casting])
        {
            reason = "client is occupied by an active game or UI event";
            return true;
        }

        return false;
    }

    private static class NativeMethods
    {
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(IntPtr processHandle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
    }
}
