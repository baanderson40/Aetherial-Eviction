using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AetherialEviction;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    public bool Enabled { get; set; }

    public bool SafetyProtection { get; set; } = true;

    public float ThresholdGb { get; set; } = 2.5f;

    public float CooldownSeconds { get; set; } = 60.0f;

    public bool BackgroundNoRenderEnabled { get; set; }

    public bool BackgroundNoRenderOnlyWhenMinimized { get; set; } = true;

    public bool BackgroundNoRenderPauseDuringCombat { get; set; } = true;

    public int BackgroundSafetyFrameIntervalSeconds { get; set; } = 5;

    public int BackgroundDutyCompletionBypassSeconds { get; set; } = 20;

    public bool WindowPlacementAutoLoadEnabled { get; set; }

    public bool WindowSizeAutoLoadEnabled { get; set; }

    public SavedWindowPlacement? WindowPlacement { get; set; }

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface dalamudPluginInterface)
    {
        _pluginInterface = dalamudPluginInterface;
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}

[Serializable]
public sealed class SavedWindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? MonitorDeviceName { get; set; }
    public int MonitorLeft { get; set; }
    public int MonitorTop { get; set; }
    public int MonitorRight { get; set; }
    public int MonitorBottom { get; set; }
    public DateTime SavedUtc { get; set; }
}
