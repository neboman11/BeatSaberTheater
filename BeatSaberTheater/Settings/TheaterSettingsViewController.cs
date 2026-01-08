using System;
using System.Collections.Generic;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using BeatSaberTheater.Util;
using JetBrains.Annotations;
using Zenject;

namespace BeatSaberTheater.Settings;

[ViewDefinition("BeatSaberTheater.Settings.Views.settings.bsml")]
internal class TheaterSettingsViewController
    : BSMLAutomaticViewController
{
    [Inject] private LoggingService _loggingService = null!;
    [Inject] private PluginConfig _config = null!;

    private const float FADE_DURATION = 0.2f;
    [UIValue("modes")][UsedImplicitly] private List<object> _qualityModes = VideoQuality.GetModeList();
    [UIValue("formats")][UsedImplicitly] private List<object> _videoFormats = VideoFormats.GetFormatList();

    [UIValue("show-video")]
    public bool PluginEnabled
    {
        get => _config.PluginEnabled;
        set
        {
            if (value)
                SetSettingsTexture();
            // PlaybackController.Instance.VideoPlayer.FadeIn(FADE_DURATION);
            // else
            // PlaybackController.Instance.VideoPlayer.FadeOut(FADE_DURATION);
            // VideoMenu.VideoMenuUI.Instance?.HandleDidSelectLevel(null);

            _config.PluginEnabled = value;
        }
    }

    [UIValue("override-environment")]
    public bool OverrideEnvironment
    {
        get => _config.OverrideEnvironment;
        set => _config.OverrideEnvironment = value;
    }

    [UIValue("disable-custom-platforms")]
    public bool DisableCustomPlatforms
    {
        get => _config.DisableCustomPlatforms;
        set => _config.DisableCustomPlatforms = value;
    }

    [UIValue("enable-360-rotation")]
    public bool Enable360Rotation
    {
        get => _config.Enable360Rotation;
        set => _config.Enable360Rotation = value;
    }

    [UIValue("bloom-intensity")]
    public int BloomIntensity
    {
        get => _config.BloomIntensity;
        set => _config.BloomIntensity = value;
    }

    [UIValue("corner-roundness")]
    public int CornerRoundness
    {
        get => (int)Math.Round(_config.CornerRoundness * 100);
        set => _config.CornerRoundness = value / 100f;
        // PlaybackController.Instance.VideoPlayer.screenController.SetVignette();
    }

    [UIValue("curved-screen")]
    public bool CurvedScreen
    {
        get => _config.CurvedScreen;
        set
        {
            _config.CurvedScreen = value;
            if (PluginEnabled) SetSettingsTexture();
        }
    }

    [UIValue("transparency-enabled")]
    public bool TransparencyEnabled
    {
        get => _config.TransparencyEnabled;
        set => _config.TransparencyEnabled = value;
        // if (value)
        //     PlaybackController.Instance.VideoPlayer.HideScreenBody();
        // else
        //     PlaybackController.Instance.VideoPlayer.ShowScreenBody();
    }

    [UIValue("color-blending-enabled")]
    public bool ColorBlendingEnabled
    {
        get => _config.ColorBlendingEnabled;
        set => _config.ColorBlendingEnabled = value;
        // PlaybackController.Instance.VideoPlayer.screenController.EnableColorBlending(value);
    }

    [UIValue("cover-enabled")]
    public bool CoverEnabled
    {
        get => _config.CoverEnabled;
        set => _config.CoverEnabled = value;
    }

    [UIValue("quality")]
    public string QualityMode
    {
        get => VideoQuality.ToName(_config.QualityMode);
        set => _config.QualityMode = VideoQuality.FromName(value);
    }

    [UIValue("format")]
    public string Format
    {
        get => VideoFormats.ToName(_config.Format);
        set => _config.Format = VideoFormats.FromName(value);
    }

    [UIValue("download-timeout-seconds")]
    public string DownloadTimeoutSeconds
    {
        get => _config.DownloadTimeoutSeconds.ToString();
        set
        {
            if (int.TryParse(value, out var timeout))
            {
                _config.DownloadTimeoutSeconds = timeout;
            }
        }
    }

    [UIValue("search-timeout-seconds")]
    public string SearchTimeoutSeconds
    {
        get => _config.SearchTimeoutSeconds.ToString();
        set
        {
            if (int.TryParse(value, out var timeout))
            {
                _config.SearchTimeoutSeconds = timeout;
            }
        }
    }

    [UIValue("ytdlp-auto-config")]
    public bool YtDlpAutoConfig
    {
        get => _config.YtDlpAutoConfig;
        set => _config.YtDlpAutoConfig = value;
    }

    private void SetSettingsTexture()
    {
        // PlaybackController.Instance.VideoPlayer.SetStaticTexture(
        //     FileHelpers.LoadPNGFromResources("BeatSaberTheater.Resources.beat-saber-logo-landscape.png"));
    }

    public override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
        if (!_config.PluginEnabled) return;

        // PlaybackController.Instance.StopPlayback();
        // PlaybackController.Instance.VideoPlayer.FadeIn(FADE_DURATION);
        SetSettingsTexture();

        // if (!_config.TransparencyEnabled) PlaybackController.Instance.VideoPlayer.ShowScreenBody();
    }

    public override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        try
        {
            //Throws NRE if the settings menu is open while the plugin gets disabled (e.g. by closing the game)
            // PlaybackController.Instance.VideoPlayer.FadeOut(FADE_DURATION);
            // PlaybackController.Instance.VideoPlayer.SetDefaultMenuPlacement();
        }
        catch (Exception e)
        {
            _loggingService.Debug(e);
        }
    }
}