using System.Runtime.CompilerServices;
using BeatSaberTheater.Settings;
using IPA.Config.Stores;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]

namespace BeatSaberTheater;

internal class PluginConfig
{
    public virtual bool PluginEnabled { get; set; } = true;
    public virtual bool OverrideEnvironment { get; set; } = true;
    public virtual bool DisableCustomPlatforms { get; set; } = true;
    public virtual bool Enable360Rotation { get; set; } = true;
    public virtual bool CurvedScreen { get; set; } = true;
    public virtual bool TransparencyEnabled { get; set; } = true;
    public virtual bool ColorBlendingEnabled { get; set; } = true;
    public virtual bool CoverEnabled { get; set; }
    public virtual int BloomIntensity { get; set; } = 100;
    public virtual float CornerRoundness { get; set; }
    public virtual VideoQuality.Mode QualityMode { get; set; } = VideoQuality.Mode.Q720P;
    public virtual VideoFormats.Format Format { get; set; } = VideoFormats.Format.Mp4;
    public virtual bool ForceDisableEnvironmentOverrides { get; set; } = false;

    // Timeouts
    public virtual int DownloadTimeoutSeconds { get; set; } = 5 * 60;
    public virtual int SearchTimeoutSeconds { get; set; } = 45;

    // yt-dlp Configuration
    public virtual bool YtDlpAutoConfig { get; set; } = false;
}