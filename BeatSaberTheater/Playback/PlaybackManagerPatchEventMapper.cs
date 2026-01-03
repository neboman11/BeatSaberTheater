using System;
using System.Collections;
using System.Linq;
using BeatSaberTheater.Environment;
using BeatSaberTheater.Harmony.Patches;
using BeatSaberTheater.Harmony.Signals;
using BeatSaberTheater.Util;
using BeatSaberTheater.Video;
using SongCore;
using UnityEngine;
using Zenject;

namespace BeatSaberTheater.Playback;

internal class PlaybackManagerPatchEventMapper : IInitializable, IDisposable
{
    private readonly PluginConfig _config;
    private readonly TheaterCoroutineStarter _coroutineStarter;
    private readonly EnvironmentManipulator _environmentManipulator;
    private readonly LoggingService _loggingService;
    private readonly PlaybackManager _playbackManager;
    // private readonly VideoMenuUI _videoMenu;

    private AudioSource? _activeAudioSource;
    private int _channelCount;
    private int _activeChannel;
    private AudioClip? _currentAudioClip;

    public PlaybackManagerPatchEventMapper(PluginConfig config,
        TheaterCoroutineStarter coroutineStarter,
        EnvironmentManipulator environmentManipulator,
        LoggingService loggingService,
        PlaybackManager playbackManager)
    {
        _config = config;
        _coroutineStarter = coroutineStarter;
        _environmentManipulator = environmentManipulator;
        _loggingService = loggingService;
        _playbackManager = playbackManager;
        // _videoMenu = videoMenuUI;
    }

    private void SetFields(SongPreviewPlayerSignal signal)
    {
        _channelCount = signal.ChannelCount;
        _activeChannel = signal.ActiveChannel;
        _currentAudioClip = signal.AudioClip;
        UpdatePlaybackManager(signal.AudioSourceControllers, signal.StartTime, signal.TimeToDefault, signal.IsDefault);
    }

    private void UpdateMapRequirements(MapRequirementsUpdateSignal signal)
    {
        try
        {
            var videoConfig = _playbackManager.GetVideoConfig();
            if (videoConfig == null) return;

            if (signal.StandardLevelDetailView._beatmapLevel.hasPrecalculatedData) return;

            var songData =
                Collections.GetCustomLevelSongData(
                    Collections.GetCustomLevelHash(signal.StandardLevelDetailView._beatmapLevel.levelID));
            if (songData == null) return;

            var diffData = Collections.GetCustomLevelSongDifficultyData(signal.StandardLevelDetailView.beatmapKey);
            Events.SetExtraSongData(songData, diffData);

            if (diffData?.HasTheaterRequirement() != true) return;

            if (videoConfig?.IsPlayable == true ||
                videoConfig?.forceEnvironmentModifications == true)
            {
                _loggingService.Debug("Requirement fulfilled");
                return;
            }

            _loggingService.Info("Theater requirement not met for " +
                                 signal.StandardLevelDetailView._beatmapLevel.songName);
            signal.StandardLevelDetailView._actionButton.interactable = false;
            signal.StandardLevelDetailView._practiceButton.interactable = false;
        }
        catch (Exception e)
        {
            _loggingService.Error(e);
        }
    }

    private void UpdatePlaybackManager(SongPreviewPlayer.AudioSourceVolumeController[] audioSourceControllers,
        float startTime, float timeToDefault, bool isDefault)
    {
        if (_currentAudioClip == null)
        {
            _loggingService.Warn("SongPreviewPlayer AudioClip was null");
            return;
        }

        if (_activeChannel < 0 || _activeChannel > _channelCount - 1)
        {
            _loggingService.Warn($"No SongPreviewPlayer audio channel active ({_activeChannel})");
            return;
        }

        if (_currentAudioClip.name == "LevelCleared" || _currentAudioClip.name.EndsWith(".egg"))
            isDefault = true;

        _activeAudioSource = audioSourceControllers[_activeChannel].audioSource;
        _loggingService.Debug(
            $"SongPreviewPatch -- channel {_activeChannel} -- startTime {startTime} -- timeRemaining {timeToDefault} -- audioclip {_currentAudioClip.name}");
        _playbackManager.UpdateSongPreviewPlayer(audioSourceControllers, _activeAudioSource, startTime, timeToDefault,
            isDefault);
    }

    private IEnumerator WaitThenStartVideoPlaybackCoroutine()
    {
        // Have to wait two frames, since Chroma waits for one and we have to make sure we run after Chroma without directly interacting with it.
        // Chroma probably waits a frame to make sure the lights are all registered before accessing the LightManager.
        // If we run before Chroma, the prop groups will get different IDs than usual due to the changed z-positions.
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();

        // Turns out CustomPlatforms runs even later and undoes some of the scene modifications Cinema does. Waiting for a specific duration is more of a temporary fix.
        // TODO: Find a better way to implement this. The problematic coroutine in CustomPlatforms is CustomFloorPlugin.EnvironmentHider+<InternalHideObjectsForPlatform>
        yield return new WaitForSeconds(InstalledMods.CustomPlatforms ? 0.75f : 0.05f);

        _environmentManipulator.ModifyGameScene(_playbackManager.GetVideoConfig());
        _loggingService.Debug("GameScene modification finished");
    }

    private void WaitThenStartVideoPlayback()
    {
        _loggingService.Debug("Starting video playback delay");
        _coroutineStarter.StartCoroutine(WaitThenStartVideoPlaybackCoroutine());
    }

    #region EnvironmentOverridePatch

    private void SceneTransitionCalled(
        BeatmapLevel beatmapLevel,
        BeatmapKey beatmapKey,
        ref OverrideEnvironmentSettings overrideEnvironmentSettings)
    {
        //Wrap all of it in try/catch so an exception would not prevent the player from playing songs
        try
        {
            var video = _playbackManager.GetVideoConfig();
            Events.InvokeSceneTransitionEvents(_config, video);
            // _videoMenu.SetSelectedLevel(beatmapLevel);

            if (!_config.PluginEnabled || _config.ForceDisableEnvironmentOverrides)
            {
                _loggingService.Info(
                    $"Cinema disabled: {!_config.PluginEnabled}, environment override force disabled: {_config.ForceDisableEnvironmentOverrides}");
                return;
            }

            if (video == null || (!video.IsPlayable && video.forceEnvironmentModifications != true))
            {
                _loggingService.Debug($"No video or not playable, DownloadState: {video?.DownloadState}");
                return;
            }

            if (video.environmentName != null)
            {
                var overrideSettings = GetOverrideEnvironmentSettingsFor(video.environmentName);
                if (overrideSettings != null)
                {
                    overrideEnvironmentSettings = overrideSettings;
                    _loggingService.Debug($"Overriding environment to {video.environmentName} as configured");
                    return;
                }
            }

            if (video.EnvironmentModified)
            {
                _loggingService.Debug("Environment is modified, disabling environment override");
                overrideEnvironmentSettings = null!;
                return;
            }

            var overrideEnvironmentEnabled = _config.OverrideEnvironment;
            if (!overrideEnvironmentEnabled)
            {
                _loggingService.Debug("Cinema's environment override disallowed by user");
                return;
            }

            var environmentWhitelist = new[]
            {
                "BigMirrorEnvironment",
                "OriginsEnvironment",
                "BTSEnvironment",
                "KDAEnvironment",
                "RocketEnvironment",
                "DragonsEnvironment",
                "Dragons2Environment",
                "LinkinParkEnvironment",
                "KaleidoscopeEnvironment",
                "GlassDesertEnvironment",
                "MonstercatEnvironment",
                "CrabRaveEnvironment",
                "SkrillexEnvironment",
                "WeaveEnvironment",
                "PyroEnvironment",
                "EDMEnvironment",
                "LizzoEnvironment"
            };

            var environmentName =
                beatmapLevel.GetEnvironmentName(beatmapKey.beatmapCharacteristic, beatmapKey.difficulty);
            // Kind of ugly way to get the EnvironmentsListModel but it's either that or changing both patches.
            var customLevelLoader = (CustomLevelLoader)VideoLoader.BeatmapLevelsModel._customLevelLoader;
            var mapEnvironmentInfoSo =
                customLevelLoader._environmentsListModel.GetEnvironmentInfoBySerializedNameSafe(environmentName);
            if (overrideEnvironmentSettings is { overrideEnvironments: true })
            {
                var overrideEnvironmentInfo =
                    overrideEnvironmentSettings.GetOverrideEnvironmentInfoForType(mapEnvironmentInfoSo.environmentType);
                if (environmentWhitelist.Contains(overrideEnvironmentInfo.serializedName))
                {
                    _loggingService.Debug("Environment override by user is in whitelist, allowing override");
                    return;
                }
            }

            if (environmentWhitelist.Contains(mapEnvironmentInfoSo.serializedName))
            {
                _loggingService.Debug("Environment chosen by mapper is in whitelist");
                overrideEnvironmentSettings = null!;
                return;
            }

            var bigMirrorOverrideSettings = GetOverrideEnvironmentSettingsFor("BigMirrorEnvironment");
            if (bigMirrorOverrideSettings == null) return;

            overrideEnvironmentSettings = bigMirrorOverrideSettings;
            _loggingService.Info("Overwriting environment to Big Mirror");
        }
        catch (Exception e)
        {
            _loggingService.Warn(e);
        }
    }

    private OverrideEnvironmentSettings? GetOverrideEnvironmentSettingsFor(string serializedName)
    {
        var environmentInfo = GetEnvironmentInfoFor(serializedName);
        if (environmentInfo == null)
        {
            _loggingService.Error($"Could not find environment environment info for {serializedName}");
            return null;
        }

        var overrideSettings = new OverrideEnvironmentSettings { overrideEnvironments = true };
        overrideSettings.SetEnvironmentInfoForType(environmentInfo.environmentType, environmentInfo);
        return overrideSettings;
    }

    private static EnvironmentInfoSO? GetEnvironmentInfoFor(string serializedName)
    {
        return Resources.FindObjectsOfTypeAll<EnvironmentInfoSO>()
            .FirstOrDefault(x => x.serializedName == serializedName);
    }

    #endregion

    public void Initialize()
    {
        LightSwitchEventEffectStart.DelayPlaybackStart = WaitThenStartVideoPlayback;
        SongPreviewPatch.OnCrossfade = SetFields;
        StandardLevelDetailViewRefreshContent.OnMapRequirementsUpdate = UpdateMapRequirements;
        StandardLevelScenesTransitionSetupDataSOInit.SceneTransitionCalled = SceneTransitionCalled;
    }

    public void Dispose()
    {
        LightSwitchEventEffectStart.DelayPlaybackStart = null;
        SongPreviewPatch.OnCrossfade = null;
        StandardLevelDetailViewRefreshContent.OnMapRequirementsUpdate = null;
        StandardLevelScenesTransitionSetupDataSOInit.SceneTransitionCalled = null;
    }
}