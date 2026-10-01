using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherialEviction;

internal sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;

    public ConfigWindow(Plugin plugin, Configuration configuration)
        : base("Aetherial Eviction")
    {
        _plugin = plugin;
        _configuration = configuration;
        Size = new Vector2(520, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##AetherialEvictionTabs"))
            return;

        if (ImGui.BeginTabItem("Memory"))
        {
            DrawMemoryTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Background Render"))
        {
            DrawBackgroundRenderTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Window Position"))
        {
            DrawWindowPositionTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawMemoryTab()
    {
        var enabled = _configuration.Enabled;
        if (ImGui.Checkbox("Enable automatic trimming", ref enabled))
        {
            _configuration.Enabled = enabled;
            _configuration.Save();
        }

        var safety = _configuration.SafetyProtection;
        if (ImGui.Checkbox("Safety protection", ref safety))
        {
            _configuration.SafetyProtection = safety;
            _configuration.Save();
        }

        ImGui.TextDisabled("Skips foreground, combat, loading, and active game/UI events.");
        if (!safety)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.65f, 0.2f, 1.0f),
                "Warning: trimming is allowed while this client is active.");
        }

        var threshold = _configuration.ThresholdGb;
        ImGui.SetNextItemWidth(160.0f);
        if (ImGui.InputFloat("Threshold (GB)", ref threshold, 0.1f, 0.5f, "%.2f"))
        {
            _configuration.ThresholdGb = Math.Clamp(threshold, 0.25f, 64.0f);
            _configuration.Save();
        }

        var cooldown = _configuration.CooldownSeconds;
        ImGui.SetNextItemWidth(160.0f);
        if (ImGui.InputFloat("Cooldown (seconds)", ref cooldown, 1.0f, 10.0f, "%.0f"))
        {
            _configuration.CooldownSeconds = Math.Clamp(cooldown, 1.0f, 3600.0f);
            _configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Current working set: {_plugin.CurrentWorkingSetGb:0.00} GB");
        ImGui.TextWrapped(_plugin.Status);

        if (_plugin.LastTrimUtc != DateTime.MinValue)
        {
            ImGui.TextUnformatted(
                $"Last trim: {_plugin.LastTrimUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
                $"({_plugin.LastTrimBeforeGb:0.00} -> {_plugin.LastTrimAfterGb:0.00} GB)");
        }
        else
        {
            ImGui.TextUnformatted("Last trim: none");
        }
    }

    private void DrawBackgroundRenderTab()
    {
        var enabled = _configuration.BackgroundNoRenderEnabled;
        if (ImGui.Checkbox("Background no-render", ref enabled))
        {
            _configuration.BackgroundNoRenderEnabled = enabled;
            _configuration.Save();
            _plugin.ApplyConfiguration();
        }

        var minimizedOnly = _configuration.BackgroundNoRenderOnlyWhenMinimized;
        if (ImGui.Checkbox("Only while minimized", ref minimizedOnly))
        {
            _configuration.BackgroundNoRenderOnlyWhenMinimized = minimizedOnly;
            _configuration.Save();
            _plugin.ApplyConfiguration();
        }

        var pauseDuringCombat = _configuration.BackgroundNoRenderPauseDuringCombat;
        if (ImGui.Checkbox("Pause no-render during combat", ref pauseDuringCombat))
        {
            _configuration.BackgroundNoRenderPauseDuringCombat = pauseDuringCombat;
            _configuration.Save();
            _plugin.ApplyConfiguration();
        }

        ImGui.TextDisabled("Normal rendering is restored during combat so BossMod can handle AoE dodges.");

        ImGui.TextDisabled("No-render is active only when this FFXIV client is inactive or minimized.");
        ImGui.TextDisabled("A safety frame is rendered periodically so the client can continue updating.");

        var safetyInterval = _configuration.BackgroundSafetyFrameIntervalSeconds;
        if (ImGui.SliderInt("Safety frame interval (seconds)", ref safetyInterval, 1, 60))
        {
            _configuration.BackgroundSafetyFrameIntervalSeconds = safetyInterval;
            _configuration.Save();
            _plugin.ApplyConfiguration();
        }

        var completionBypass = _configuration.BackgroundDutyCompletionBypassSeconds;
        if (ImGui.SliderInt("Duty completion render bypass (seconds)", ref completionBypass, 1, 120))
        {
            _configuration.BackgroundDutyCompletionBypassSeconds = completionBypass;
            _configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Render request: {(_plugin.BackgroundRenderGateService.RenderRequestActive ? "active" : "inactive")}");
        ImGui.TextUnformatted($"Combat bypass: {(_plugin.BackgroundRenderGateService.CombatRenderBypassActive ? "active" : "inactive")}");
        ImGui.TextUnformatted($"Duty completion bypass: {(_plugin.BackgroundRenderGateService.DutyCompletionBypassActive ? "active" : "inactive")}");
        ImGui.TextWrapped(_plugin.BackgroundRenderGateService.Status);
    }

    private void DrawWindowPositionTab()
    {
        if (_plugin.WindowPlacementService.TryReadCurrent(out var current))
        {
            ImGui.TextUnformatted($"Current position: {current.X}, {current.Y}");
            ImGui.TextUnformatted($"Current size: {current.Width} x {current.Height}");
            ImGui.TextUnformatted($"Current monitor: {current.MonitorDeviceName}");

            var x = current.X;
            var y = current.Y;
            if (ImGui.InputInt("X", ref x) && x != current.X)
                _plugin.MoveGameWindow(x, current.Y);
            if (ImGui.InputInt("Y", ref y) && y != current.Y)
                _plugin.MoveGameWindow(current.X, y);
        }
        else
        {
            ImGui.TextDisabled(_plugin.WindowPlacementService.Status);
        }

        ImGui.Separator();
        var saved = _configuration.WindowPlacement;
        if (saved == null)
        {
            ImGui.TextDisabled("No saved window placement.");
        }
        else
        {
            ImGui.TextUnformatted($"Saved position: {saved.X}, {saved.Y}");
            ImGui.TextUnformatted($"Saved size: {saved.Width} x {saved.Height}");
            ImGui.TextUnformatted($"Saved monitor: {saved.MonitorDeviceName ?? "unknown"}");
            ImGui.TextUnformatted($"Saved UTC: {saved.SavedUtc:u}");
        }

        if (ImGui.Button("Save Current Window"))
            _plugin.SaveCurrentWindowPlacement();
        ImGui.SameLine();
        if (ImGui.Button("Load Position"))
            _plugin.LoadSavedWindow(includeSize: false);
        ImGui.SameLine();
        if (ImGui.Button("Load Position + Size"))
            _plugin.LoadSavedWindow(includeSize: true);

        var positionAutoLoad = _configuration.WindowPlacementAutoLoadEnabled;
        if (ImGui.Checkbox("Load saved position on client load", ref positionAutoLoad))
        {
            _configuration.WindowPlacementAutoLoadEnabled = positionAutoLoad;
            _configuration.Save();
        }

        var sizeAutoLoad = _configuration.WindowSizeAutoLoadEnabled;
        if (ImGui.Checkbox("Load saved size on client load", ref sizeAutoLoad))
        {
            _configuration.WindowSizeAutoLoadEnabled = sizeAutoLoad;
            _configuration.Save();
        }

        if (ImGui.Button("Reset Saved Placement"))
            _plugin.ResetWindowPlacement();

        ImGui.Separator();
        ImGui.TextWrapped(_plugin.WindowPlacementService.Status);
    }
}
