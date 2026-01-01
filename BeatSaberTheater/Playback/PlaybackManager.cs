using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeatSaberTheater.Download;
using BeatSaberTheater.Environment.Interfaces;
using BeatSaberTheater.Screen;
using BeatSaberTheater.Screen.Interfaces;
using BeatSaberTheater.Util;
using BeatSaberTheater.Video;
using BeatSaberTheater.Video.Config;
using BS_Utils;
using BS_Utils.Gameplay;
using BS_Utils.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Zenject;
using Scene = BeatSaberTheater.Util.Scene;

namespace BeatSaberTheater.Playback;

public class PlaybackManager : MonoBehaviour
{
    public bool IsPreviewPlaying { get; private set; }

    private AudioSource? _activeAudioSource;
    private Scene _activeScene = Scene.Other;
    private SongPreviewPlayer.AudioSourceVolumeController[]? _audioSourceControllers;
    private DateTime _audioSourceStartTime;
    private BeatmapLevel? _currentLevel;
    private float _lastKnownAudioSourceTime;
    private Environment.LightManager _lightManager = null!;
    private float _offsetAfterPrepare;
    private Stopwatch? _playbackDelayStopwatch;
    private IEnumerator? _prepareVideoCoroutine;
    private float _previewStartTime;
    private DateTime _previewSyncStartTime;
    private float _previewTimeRemaining;
    private bool _previewWaitingForPreviewPlayer;
    private bool _previewWaitingForVideoPlayer = true;
    private SettingsManager? _settingsManager;
    private SongPreviewPlayer? _songPreviewPlayer;
    private AudioTimeSyncController? _timeSyncController;
    private VideoConfig? _videoConfig;
    private CustomVideoPlayer _videoPlayer = null!;

    [Inject] private readonly PluginConfig _config = null!;
    [Inject] private readonly ILightManagerFactory _lightManagerFactory = null!;
    [Inject] private readonly LoggingService _loggingService = null!;
    [Inject] private readonly VideoLoader _videoLoader = null!;

    // [Inject] private readonly VideoMenuUI _videoMenu = null!;
    [Inject] private readonly ICustomVideoPlayerFactory _videoPlayerFactory = null!;

    #region Unity Event Functions

    private void Start()
    {
        BSEvents.gameSceneActive += GameSceneActive;
        BSEvents.gameSceneLoaded += GameSceneLoaded;
        BSEvents.lateMenuSceneLoadedFresh += OnMenuSceneLoadedFresh;
        BSEvents.menuSceneLoaded += OnMenuSceneLoaded;
        BSEvents.songPaused += PauseVideo;
        BSEvents.songUnpaused += ResumeVideo;
        VideoLoader.ConfigChanged += OnConfigChanged;
        Events.DifficultySelected += DifficultySelected;
    }

    private void OnDestroy()
    {
        if (_videoPlayer != null)
            _videoPlayer.Shutdown(FrameReady, OnPrepareComplete, VideoPlayerErrorReceived);

        BSEvents.gameSceneActive -= GameSceneActive;
        BSEvents.gameSceneLoaded -= GameSceneLoaded;
        BSEvents.lateMenuSceneLoadedFresh -= OnMenuSceneLoadedFresh;
        BSEvents.menuSceneLoaded -= OnMenuSceneLoaded;
        BSEvents.songPaused -= PauseVideo;
        BSEvents.songUnpaused -= ResumeVideo;
        VideoLoader.ConfigChanged -= OnConfigChanged;
        Events.DifficultySelected -= DifficultySelected;
    }

    #endregion

    public void StopPlayback()
    {
        _videoPlayer.Stop();
        StopAllCoroutines();
    }

    public void StopAndUnloadVideo()
    {
        _videoPlayer.Stop();
        _videoPlayer.UnloadVideo();
    }

    public void HideVideoPlayer()
    {
        _videoPlayer.Hide();
    }

    #region Event handlers

    private void ConfigChangedFrameReadyHandler(VideoPlayer sender, long frameIdx)
    {
        _loggingService.Info("First frame after config change is ready");
        sender.frameReady -= ConfigChangedFrameReadyHandler;
        if (_activeAudioSource == null) return;

        if (!_activeAudioSource.isPlaying)
        {
            _videoPlayer.Pause();
            _videoPlayer.SetBrightness(1f);
        }

        _videoPlayer.UpdateScreenContent();
    }

    private void ConfigChangedPrepareHandler(VideoPlayer sender)
    {
        sender.prepareCompleted -= ConfigChangedPrepareHandler;
        if (_activeScene == Scene.Menu || _activeAudioSource == null) return;

        sender.frameReady += ConfigChangedFrameReadyHandler;
        PlayVideo(_lastKnownAudioSourceTime);
    }

    private void DifficultySelected(ExtraSongDataArgs extraSongDataArgs)
    {
        if (_videoConfig == null) return;

        var difficultyData = extraSongDataArgs.SelectedDifficultyData;
        var songData = extraSongDataArgs.SongData;

        // If there is any difficulty that has a Theater suggestion but the current one doesn't, disable playback. The current difficulty most likely has the suggestion missing on purpose.
        // If there are no difficulties that have the suggestion set, play the video. It might be a video added by the user.
        // Otherwise, if the map is WIP, disable playback even when no difficulty has the suggestion, to convince the mapper to add it.
        if (difficultyData?.HasTheater() == false && songData?.HasTheaterInAnyDifficulty() == true)
            _videoConfig.PlaybackDisabledByMissingSuggestion = true;
        else if (_videoConfig.IsWIPLevel && difficultyData?.HasTheater() == false)
            _videoConfig.PlaybackDisabledByMissingSuggestion = true;
        else
            _videoConfig.PlaybackDisabledByMissingSuggestion = false;

        if (_videoConfig.PlaybackDisabledByMissingSuggestion)
        {
            _videoPlayer.FadeOut(0.1f);
        }
        else
        {
            if (!_videoPlayer.IsPlaying) StartSongPreview();
        }
    }

    public void FrameReady(VideoPlayer videoPlayer, long frame)
    {
        if (_activeAudioSource == null || _videoConfig == null) return;

        var audioSourceTime = _activeAudioSource.time;

        if (_videoPlayer.IsFading) return;

        var playerTime = _videoPlayer.PlayerTime;
        var referenceTime = GetReferenceTime();
        if (_videoPlayer.VideoDuration > 0) referenceTime %= _videoPlayer.VideoDuration;
        var error = referenceTime - playerTime;

        if (!_activeAudioSource.isPlaying) return;

        if (frame % 120 == 0)
            _loggingService.Debug("Frame: " + frame + " - Player: " +
                                  TheaterFileHelpers.FormatFloat((float)playerTime) +
                                  " - AudioSource: " +
                                  TheaterFileHelpers.FormatFloat(audioSourceTime) + " - Error (ms): " +
                                  Math.Round(error * 1000));

        if (_videoConfig.endVideoAt.HasValue)
        {
            if (referenceTime >= _videoConfig.endVideoAt - 1f)
            {
                var brightness = Math.Max(0f, _videoConfig.endVideoAt.Value - referenceTime);
                _videoPlayer.SetBrightness(brightness);
            }
        }
        else if (referenceTime >= _videoPlayer.PlayerLength - 1f && _videoConfig.loop != true)
        {
            var brightness = Math.Max(0f, _videoPlayer.PlayerLength - referenceTime);
            _videoPlayer.SetBrightness((float)brightness);
        }

        if (Math.Abs(audioSourceTime - _lastKnownAudioSourceTime) > 0.3f && _videoPlayer.IsPlaying)
        {
            _loggingService.Debug("Detected AudioSource seek, resyncing...");
            ResyncVideo();
        }

        //Sync if the error exceeds a threshold, but not if the video is close to the looping point
        if (Math.Abs(error) > 0.3f && Math.Abs(_videoPlayer.VideoDuration - playerTime) > 0.5f &&
            _videoPlayer.IsPlaying)
            //Audio can intentionally go out of sync when the level fails for example. Don't resync the video in that case.
            if (_timeSyncController != null && !_timeSyncController.forcedNoAudioSync)
            {
                _loggingService.Debug(
                    $"Detected desync (reference {referenceTime}, actual {playerTime}), resyncing...");
                ResyncVideo();
            }

        if (audioSourceTime > 0) _lastKnownAudioSourceTime = audioSourceTime;
    }

    private void GameSceneActive()
    {
        // If BSUtils has no level data, we're probably in the tutorial
        if (BS_Utils.Plugin.LevelData.IsSet)
        {
            // Move to the environment scene to be picked up by Chroma
            var sceneName = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData.targetEnvironmentInfo.environmentSceneName;
            var scene = SceneManager.GetSceneByName(sceneName);
            SceneManager.MoveGameObjectToScene(gameObject, scene);
        }

        _loggingService.Info("Moving to game scene");
    }

    public void GameSceneLoaded()
    {
        StopAllCoroutines();
        _loggingService.Info("GameSceneLoaded");

        _activeScene = Scene.SoloGameplay;

        if (!_config.PluginEnabled)
        {
            _loggingService.Info("Plugin disabled");
            _videoPlayer.Hide();
            return;
        }

        _lightManager.OnGameSceneLoaded();

        StopPlayback();
        _videoPlayer.Hide();
        _videoPlayer.ResetScreens();

        if (!TheaterFileHelpers.IsInEditor())
        {
            if (BS_Utils.Plugin.LevelData.Mode == Mode.None)
            {
                _loggingService.Info("Level mode is None");
                return;
            }

            var bsUtilsLevel = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData.beatmapLevel;
            if (_currentLevel?.levelID != bsUtilsLevel.levelID)
            {
                var video = _videoLoader.GetConfigForLevel(bsUtilsLevel);
                SetSelectedLevel(bsUtilsLevel, video);
            }
        }

        if (_videoConfig == null || !_videoConfig.IsPlayable)
        {
            _loggingService.Info("No video configured or video is not playable: " + _videoConfig?.VideoPath);

            if (_config.CoverEnabled && (_videoConfig?.forceEnvironmentModifications == null ||
                                         _videoConfig.forceEnvironmentModifications == false))
                ShowSongCover().Start();
            return;
        }

        // Some mappers disable this accidentally
        gameObject.SetActive(true);

        if (_videoConfig.NeedsToSave) _videoLoader.SaveVideoConfig(_videoConfig);

        UpdateVideoPlayerPlacement(_videoConfig, _activeScene);

        _videoPlayer.Show();
        // Fixes rough pop-in at the start of the song when transparency is disabled
        if (!(_videoConfig.TransparencyEnabled && _config.TransparencyEnabled) && _activeScene != Scene.Menu)
        {
            _videoPlayer.ScreenColor = Color.black;
            _videoPlayer.ShowScreenBody();
        }

        SetAudioSourcePanning(0);
        _videoPlayer.Mute();
        _loggingService.Info("Playing video from GameSceneLoaded");
        StartCoroutine(PlayVideoAfterAudioSourceCoroutine(false));
        SceneChanged();
    }

    private void OnConfigChanged(VideoConfig? config)
    {
        OnConfigChanged(config, false);
    }

    private void OnConfigChanged(VideoConfig? config, bool? reloadVideo)
    {
        var previousVideoPath = _videoConfig?.VideoPath;
        _videoConfig = config;

        if (config == null)
        {
            _videoPlayer.Hide();
            return;
        }

        if (!config.IsPlayable &&
            (config.forceEnvironmentModifications == null || config.forceEnvironmentModifications == false)) return;

        if (_activeScene == Scene.Menu)
        {
            StopPreview(true);
        }
        else
        {
            UpdateVideoPlayerPlacement(config, _activeScene);
            ResyncVideo();
        }

        if (previousVideoPath != config.VideoPath || reloadVideo == true)
        {
            _videoPlayer.AddPrepareCompletedEventHandler(ConfigChangedPrepareHandler);
            PrepareVideo(config);
        }
        else
        {
            _videoPlayer.LoopVideo(config.loop == true);
            _videoPlayer.SetScreenShaderParameters(config);
            _videoPlayer.SetBloomIntensity(config.bloom);
        }

        if (config.TransparencyEnabled)
            _videoPlayer.HideScreenBody();
        else
            _videoPlayer.ShowScreenBody();

        // if (_activeScene == Scene.SoloGameplay) EnvironmentController.VideoConfigSceneModifications(_videoConfig);
    }

    private void OnMenuSceneLoaded()
    {
        _loggingService.Info("MenuSceneLoaded");
        _activeScene = Scene.Menu;
        _videoPlayer.Hide();
        _videoPlayer.ResetScreens();
        StopAllCoroutines();
        _previewWaitingForPreviewPlayer = true;
        gameObject.SetActive(true);
        SceneChanged();
    }

    private void OnMenuSceneLoadedFresh(ScenesTransitionSetupDataSO? scenesTransition)
    {
        _songPreviewPlayer = Resources.FindObjectsOfTypeAll<SongPreviewPlayer>().LastOrDefault();
        _videoPlayer = _videoPlayerFactory.Create(gameObject);
        _videoPlayer.Startup(FrameReady, OnPrepareComplete, VideoPlayerErrorReceived);
        _lightManager = _lightManagerFactory.Create(gameObject);
        _lightManager.Startup(_videoPlayer);
        OnMenuSceneLoaded();
        if (_settingsManager == null)
        {
            StartCoroutine(OnMenuSceneLoadedFreshCoroutine());
        }
        else
        {
            _videoPlayer.SetVolumeScale(_settingsManager.settings.audio.volume);
            _videoPlayer.ScreenMenuLoadedFresh();
        }
    }

    private IEnumerator OnMenuSceneLoadedFreshCoroutine()
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        yield return new WaitUntil(() => Plugin._menuContainer != null);
        _settingsManager = Plugin._menuContainer.Resolve<SettingsManager>();
        _videoPlayer.SetVolumeScale(_settingsManager.settings.audio.volume);
        _videoPlayer.ScreenMenuLoadedFresh();
    }

    private void OnPrepareComplete(VideoPlayer player)
    {
        if (_offsetAfterPrepare > 0)
        {
            var offset = (DateTime.Now - _audioSourceStartTime).TotalSeconds + _offsetAfterPrepare;
            _loggingService.Info($"Adjusting offset after prepare to {offset}");
            player.time = offset;
        }

        _offsetAfterPrepare = 0;
        _videoPlayer.ClearTexture();

        if (_activeScene != Scene.Menu) return;

        _previewWaitingForVideoPlayer = false;
        StartSongPreview();
    }

    private void SceneChanged()
    {
        _videoPlayer.SetScreenShaderParameters(_videoConfig);
    }

    #endregion

    #region Harmony Patch Hooks

    public void UpdateSongPreviewPlayer(SongPreviewPlayer.AudioSourceVolumeController[] audioSourceControllers,
        AudioSource? activeAudioSource, float startTime, float timeRemaining, bool isDefault)
    {
        _audioSourceControllers = audioSourceControllers;
        _activeAudioSource = activeAudioSource;
        _lastKnownAudioSourceTime = 0;
        if (_activeAudioSource == null) _loggingService.Debug("Active AudioSource null in SongPreviewPlayer update");

        if (IsPreviewPlaying)
        {
            if (isDefault)
            {
                StopPreview(false);
                return;
            }

            _previewWaitingForPreviewPlayer = true;
            _loggingService.Debug($"Ignoring SongPreviewPlayer update");
            return;
        }

        if (isDefault)
        {
            StopPreview(true);
            _videoPlayer.FadeOut();
            _previewWaitingForPreviewPlayer = true;

            _loggingService.Debug("SongPreviewPlayer reverting to default loop");
            return;
        }

        // This allows the short preview for the practice offset to play
        if (!_previewWaitingForPreviewPlayer && Math.Abs(timeRemaining - 2.5f) > 0.001f)
        {
            StopPreview(true);
            _videoPlayer.FadeOut();

            _loggingService.Debug("Unexpected SongPreviewPlayer update, ignoring.");
            return;
        }

        if (_activeScene != Scene.Menu) return;

        if (_currentLevel != null && _currentLevel.songDuration < startTime)
        {
            _loggingService.Debug("Song preview start time was greater than song duration. Resetting start time to 0");
            startTime = 0;
        }

        _previewStartTime = startTime;
        _previewTimeRemaining = timeRemaining;
        _previewSyncStartTime = DateTime.Now;
        _previewWaitingForPreviewPlayer = false;
        StartSongPreview();
    }

    #endregion

    #region Video Playback

    private void PlayVideo(float startTime)
    {
        if (_videoConfig == null)
        {
            _loggingService.Warn("VideoConfig null in PlayVideo");
            return;
        }

        _videoPlayer.IsSyncing = false;

        // Always hide screen body in the menu, since the drawbacks of the body being visible are large
        if (!(_videoConfig.TransparencyEnabled && _config.TransparencyEnabled) && _activeScene != Scene.Menu)
            _videoPlayer.ShowScreenBody();
        else
            _videoPlayer.HideScreenBody();

        var totalOffset = _videoConfig.GetOffsetInSec();
        var songSpeed = 1f;
        if (BS_Utils.Plugin.LevelData.IsSet)
        {
            songSpeed = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData.gameplayModifiers.songSpeedMul;

            if (BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData?.practiceSettings != null)
            {
                songSpeed = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData.practiceSettings.songSpeedMul;
                if (totalOffset + startTime < 0) totalOffset /= songSpeed * _videoConfig.PlaybackSpeed;
            }
        }

        _videoPlayer.PlaybackSpeed = songSpeed * _videoConfig.PlaybackSpeed;
        totalOffset += startTime; // This must happen after song speed adjustment

        if (songSpeed * _videoConfig.PlaybackSpeed < 1f && totalOffset > 0f)
        {
            // Unity crashes if the playback speed is less than 1 and the video time at the start of playback is greater than 0
            _loggingService.Warn("Video playback disabled to prevent Unity crash");
            _videoPlayer.Hide();
            StopPlayback();
            _videoConfig = null;
            return;
        }

        // Video seemingly always lags behind. A fixed offset seems to work well enough
        if (!IsPreviewPlaying) totalOffset += 0.0667f;

        if (_videoConfig.endVideoAt != null && totalOffset > _videoConfig.endVideoAt)
            totalOffset = _videoConfig.endVideoAt.Value;

        // This will fail if the video is not prepared yet
        if (_videoPlayer.VideoDuration > 0) totalOffset %= _videoPlayer.VideoDuration;

        // This fixes an issue where the Unity video player sometimes ignores a change in the .time property if the time is very small and the player is currently playing
        if (Math.Abs(totalOffset) < 0.001f)
        {
            totalOffset = 0;
            _loggingService.Debug("Set very small offset to 0");
        }

        _loggingService.Debug(
            $"Total offset: {totalOffset}, startTime: {startTime}, songSpeed: {songSpeed}, player time: {_videoPlayer.PlayerTime}");

        StopAllCoroutines();

        if (_activeAudioSource != null && _activeAudioSource.time > 0)
            _lastKnownAudioSourceTime = _activeAudioSource.time;

        if (totalOffset < 0)
        {
            if (!IsPreviewPlaying)
                // Negate the offset to turn it into a positive delay
                StartCoroutine(PlayVideoDelayedCoroutine(-totalOffset));
            else
                // In menus we don't need to wait, instead the preview player starts earlier
                _videoPlayer.Play();
        }
        else
        {
            _videoPlayer.Play();
            if (!_videoPlayer.PlayerIsPrepared)
            {
                _audioSourceStartTime = DateTime.Now;
                _offsetAfterPrepare = totalOffset;
            }
            else
            {
                _videoPlayer.PlayerTime = totalOffset;
            }
        }
    }

    // TODO: Using a stopwatch will not work properly when seeking in the map (e.g. IntroSkip, PracticePlugin)
    private IEnumerator PlayVideoDelayedCoroutine(float delayStartTime)
    {
        _loggingService.Debug("Waiting for " + delayStartTime + " seconds before playing video");
        _playbackDelayStopwatch ??= new Stopwatch();
        _playbackDelayStopwatch.Start();
        _videoPlayer.Pause();
        _videoPlayer.Hide();
        _videoPlayer.PlayerTime = 0;
        var ticksUntilStart = delayStartTime * TimeSpan.TicksPerSecond;
        yield return new WaitUntil(() => _playbackDelayStopwatch.ElapsedTicks >= ticksUntilStart);
        _loggingService.Debug("Elapsed ms: " + _playbackDelayStopwatch.ElapsedMilliseconds);
        _playbackDelayStopwatch.Stop();
        _playbackDelayStopwatch.Reset();

        if (_activeAudioSource != null && _activeAudioSource.time > 0)
            _lastKnownAudioSourceTime = _activeAudioSource.time;

        _videoPlayer.Play();
    }

    private IEnumerator PlayVideoAfterAudioSourceCoroutine(bool preview)
    {
        float startTime;

        if (!preview)
        {
            _loggingService.Debug("Waiting for ATSC to be ready");

            try
            {
                if (TheaterFileHelpers.IsInEditor() && SceneManager.GetActiveScene().name != "GameCore")
                {
                    var songPreviewPlayer = Plugin.gameCoreContainer.Resolve<SongPreviewPlayer>();
                    if (songPreviewPlayer._audioSourceControllers.Any())
                    {
                        _activeAudioSource = songPreviewPlayer._audioSourceControllers.First().audioSource;
                        _loggingService.Debug("Got ATSC from SongPreviewPlayer");
                    }
                }
                else
                {
                    var atsc = Plugin.gameCoreContainer.Resolve<AudioTimeSyncController>();
                    _activeAudioSource = atsc._audioSource;
                    _loggingService.Debug("Got ATSC from ATSC");
                }
            }
            catch
            {
                _loggingService.Debug("Failed to get AudioSource from DiContainer");
            }

            if (_activeAudioSource == null)
            {
                if (TheaterFileHelpers.IsInEditor() && SceneManager.GetActiveScene().name != "GameCore")
                {
                    // _editorTimeSyncController = Resources.FindObjectsOfTypeAll<BeatmapEditorAudioTimeSyncController>()
                    //     .FirstOrDefault(atsc => atsc.name == "BeatmapEditorAudioTimeSyncController");
                    _activeAudioSource = Resources.FindObjectsOfTypeAll<AudioSource>()
                        .FirstOrDefault(audioSource =>
                            audioSource.name == "SongPreviewAudioSource(Clone)" &&
                            audioSource.transform.parent == null);
                }
                else
                {
                    yield return new WaitUntil(() => Resources.FindObjectsOfTypeAll<AudioTimeSyncController>().Any());

                    // Hierarchy: Wrapper/StandardGameplay/GameplayCore/SongController
                    _timeSyncController = Resources.FindObjectsOfTypeAll<AudioTimeSyncController>()
                        .FirstOrDefault(atsc => atsc.transform.parent.parent.name.Contains("StandardGameplay"));

                    if (_timeSyncController == null)
                    {
                        _loggingService.Warn(
                            "Could not find ATSC the usual way. Did the object hierarchy change? Current scene name is " +
                            SceneManager.GetActiveScene().name);

                        // This throws an exception if we still don't find the ATSC
                        _timeSyncController = Resources.FindObjectsOfTypeAll<AudioTimeSyncController>().Last();
                        _loggingService.Warn("Selected ATSC: " + _timeSyncController.name);
                    }

                    _activeAudioSource = _timeSyncController._audioSource;
                }
            }
        }

        if (_activeAudioSource != null)
        {
            _lastKnownAudioSourceTime = 0;
            _loggingService.Debug($"Waiting for AudioSource {_activeAudioSource.name} to start playing");
            yield return new WaitUntil(() => _activeAudioSource.isPlaying);
            startTime = _activeAudioSource.time;
        }
        else
        {
            _loggingService.Warn("Active AudioSource was null, cannot wait for it to start");
            StopPreview(true);
            yield break;
        }

        PlayVideo(startTime);
    }

    public void PauseVideo()
    {
        StopAllCoroutines();
        if (_videoPlayer.IsPlaying && _videoConfig != null) _videoPlayer.Pause();
    }

    public void ResumeVideo()
    {
        if (!_config.PluginEnabled || _videoPlayer.IsPlaying || _videoConfig == null || !_videoConfig.IsPlayable ||
            (_videoPlayer.VideoEnded && _videoConfig.loop != true)) return;

        var referenceTime = GetReferenceTime();
        if (referenceTime > 0)
            _videoPlayer.Play();
        else if (_playbackDelayStopwatch is { IsRunning: false })
            StartCoroutine(PlayVideoDelayedCoroutine(-referenceTime));
    }

    public void ResyncVideo(float? referenceTime = null)
    {
        if (_activeAudioSource == null || _videoConfig == null || !_videoConfig.IsPlayable) return;

        float newTime;
        if (referenceTime != null)
            newTime = referenceTime.Value;
        else
            newTime = GetReferenceTime();

        if (newTime < 0)
        {
            _videoPlayer.Hide();
            StopAllCoroutines();
            StartCoroutine(PlayVideoDelayedCoroutine(-newTime));
        }
        else if (newTime > _videoPlayer.VideoDuration && _videoPlayer.VideoDuration > 0)
        {
            newTime %= _videoPlayer.VideoDuration;
        }

        if (Math.Abs(_videoPlayer.PlayerTime - newTime) < 0.2f) return;

        _videoPlayer.PlayerTime = newTime;
    }

    private void VideoPlayerErrorReceived(string message)
    {
        StopPlayback();
        if (_videoConfig == null) return;

        _videoConfig.UpdateDownloadState();
        _videoConfig.ErrorMessage = "Theater playback error.";
        if (message.Contains("Unexpected error code (10)") && SystemInfo.graphicsDeviceVendor == "NVIDIA")
            _videoConfig.ErrorMessage += " Try disabling NVIDIA Fast Sync.";
        else if (message.Contains("It seems that the Microsoft Media Foundation is not installed on this machine"))
            _videoConfig.ErrorMessage += " Install Microsoft Media Foundation.";
        else
            _videoConfig.ErrorMessage += " See logs for details.";

        // _videoMenu.SetupLevelDetailView(VideoConfig);
    }

    #endregion

    #region Video Prepare

    public void PrepareVideo(VideoConfig video)
    {
        _previewWaitingForVideoPlayer = true;

        if (_prepareVideoCoroutine != null) StopCoroutine(_prepareVideoCoroutine);

        _videoPlayer.ClearTexture();

        _prepareVideoCoroutine = PrepareVideoCoroutine(video);
        StartCoroutine(_prepareVideoCoroutine);
    }

    private IEnumerator PrepareVideoCoroutine(VideoConfig video)
    {
        _videoConfig = video;

        _videoPlayer.Pause();
        if (_videoConfig.DownloadState != DownloadState.Downloaded)
        {
            _loggingService.Debug("Video is not downloaded, stopping prepare");
            _videoPlayer.FadeOut();
            yield break;
        }

        _videoPlayer.LoopVideo(video.loop == true);
        _videoPlayer.SetScreenShaderParameters(video);
        _videoPlayer.SetBloomIntensity(video.bloom);

        if (video.VideoPath == null)
        {
            _loggingService.Debug("Video path was null, stopping prepare");
            yield break;
        }

        var videoPath = video.VideoPath;
        _loggingService.Info($"Loading video: {videoPath}");

        if (video.videoFile != null)
        {
            var videoFileInfo = new FileInfo(videoPath);
            var timeout = new DownloadTimeout(0.25f);
            if (_videoPlayer.Url != videoPath)
                yield return new WaitUntil(() =>
                    !TheaterFileHelpers.IsFileLocked(videoFileInfo) || timeout.HasTimedOut);

            timeout.Stop();
            if (timeout.HasTimedOut && TheaterFileHelpers.IsFileLocked(videoFileInfo))
                _loggingService.Warn("Video file locked: " + videoPath);
        }

        _videoPlayer.Url = videoPath;
        _videoPlayer.Prepare();
    }

    #endregion

    #region Video Preview

    public void ApplyOffset(int offset)
    {
        if (!_videoPlayer.IsPlaying || _activeAudioSource == null) return;

        // Pause the preview audio source and start seeking. Audio Source will be re-enabled after video player draws its next frame
        _videoPlayer.IsSyncing = true;
        _activeAudioSource.Pause();

        ApplyOffsetToVideo(offset);
        _videoPlayer.AddFrameReadyEventHandler(PlayerStartedAfterResync);
        _loggingService.Info("Applying offset: " + offset);
    }

    public void ApplyOffsetToVideo(float offset)
    {
        if (_activeAudioSource == null || _videoConfig == null || !_videoConfig.IsPlayable) return;

        var newTime = _videoPlayer.PlayerTime + offset / 1000f;

        if (newTime < 0)
        {
            _videoPlayer.Hide();
            StopAllCoroutines();
            StartCoroutine(PlayVideoDelayedCoroutine((float)-newTime));
        }
        else if (newTime > _videoPlayer.VideoDuration && _videoPlayer.VideoDuration > 0)
        {
            newTime %= _videoPlayer.VideoDuration;
        }

        _videoPlayer.PlayerTime = newTime;
    }

    private void PlayerStartedAfterResync(VideoPlayer player, long frame)
    {
        _videoPlayer.RemoveFrameReadyEventHandler(PlayerStartedAfterResync);
        if (_activeAudioSource == null)
        {
            _loggingService.Warn("Active audio source was null in frame ready after resync");
            return;
        }

        _videoPlayer.IsSyncing = false;
        if (!_activeAudioSource.isPlaying) _activeAudioSource.Play();
    }

    public void SetAudioSourcePanning(float pan)
    {
        try
        {
            if (_audioSourceControllers == null) return;

            // If resetting the panning back to neutral (0f), set all audio sources.
            // Otherwise only change the active channel.
            if (pan == 0f || _activeAudioSource == null)
                foreach (var sourceVolumeController in _audioSourceControllers)
                    sourceVolumeController.audioSource.panStereo = pan;
            else
                _activeAudioSource.panStereo = pan;
        }
        catch (Exception e)
        {
            _loggingService.Warn(e);
        }
    }

    public async Task StartPreview()
    {
        if (_videoConfig == null || _currentLevel == null)
        {
            _loggingService.Warn("No video or level selected in OnPreviewAction");
            return;
        }

        if (IsPreviewPlaying)
        {
            _loggingService.Debug("Stopping preview");
            StopPreview(true);
        }
        else
        {
            _loggingService.Debug("Starting preview");
            IsPreviewPlaying = true;

            if (_videoPlayer.IsPlaying) StopPlayback();

            if (!_videoPlayer.IsPrepared) _loggingService.Debug("Video not prepared yet");

            // Start the preview at the point the video kicks in
            var startTime = 0f;
            if (_videoConfig.offset < 0) startTime = -_videoConfig.GetOffsetInSec();

            if (_songPreviewPlayer == null)
            {
                _loggingService.Error("Failed to get reference to SongPreviewPlayer during preview");
                return;
            }

            try
            {
                _loggingService.Debug($"Preview start time: {startTime}, offset: {_videoConfig.GetOffsetInSec()}");
                var audioClip = await VideoLoader.GetAudioClipForLevel(_currentLevel);
                if (audioClip != null)
                    _songPreviewPlayer.CrossfadeTo(audioClip, -5f, startTime,
                        _currentLevel.songDuration, null);
                else
                    _loggingService.Error("AudioClip for level failed to load");
            }
            catch (Exception e)
            {
                _loggingService.Error(e);
                IsPreviewPlaying = false;
                return;
            }

            // +1.0 is hard right. only pan "mostly" right, because for some reason the video player audio doesn't
            // pan hard left either. Also, it sounds a bit more comfortable.
            SetAudioSourcePanning(0.9f);
            StartCoroutine(PlayVideoAfterAudioSourceCoroutine(true));
            _videoPlayer.PanStereo = -1f; // -1 is hard left
            _videoPlayer.Unmute();
        }
    }

    private void StartSongPreview()
    {
        if (!_config.PluginEnabled || _videoConfig is not { IsPlayable: true }) return;

        if (_previewWaitingForPreviewPlayer || _previewWaitingForVideoPlayer || IsPreviewPlaying) return;

        if (_currentLevel != null && VideoLoader.IsDlcSong(_currentLevel)) return;

        var delay = DateTime.Now.Subtract(_previewSyncStartTime);
        var delaySeconds = (float)delay.TotalSeconds;

        _loggingService.Debug($"Starting song preview playback with a delay of {delaySeconds}");

        var timeRemaining = _previewTimeRemaining - delaySeconds;
        if (timeRemaining > 1 || _previewTimeRemaining == 0)
            PlayVideo(_previewStartTime + delaySeconds);
        else
            _loggingService.Debug(
                $"Not playing song preview, because delay was too long. Remaining preview time: {_previewTimeRemaining}");
    }

    public void StopPreview(bool stopPreviewMusic)
    {
        if (!IsPreviewPlaying) return;
        _loggingService.Debug($"Stopping preview (stop audio source: {stopPreviewMusic}");

        _videoPlayer.FadeOut();
        StopAllCoroutines();

        if (stopPreviewMusic && _songPreviewPlayer != null)
        {
            _songPreviewPlayer.CrossfadeToDefault();
            _videoPlayer.Mute();
        }

        IsPreviewPlaying = false;

        SetAudioSourcePanning(0f); // 0f is neutral
        _videoPlayer.Mute();
    }

    #endregion

    public void AddScreenToVideoPlayer(GameObject screen, CurvedSurface curvedSurface,
        CustomBloomPrePass customBloomPrePass)
    {
        _videoPlayer.AddScreen(screen, curvedSurface, customBloomPrePass);
    }

    public void DisableVideoPlayerScreens()
    {
        _videoPlayer.DisableScreen();
    }

    public GameObject? FindVideoPlayerScreen(Predicate<ScreenObjectGroup> predicate)
    {
        return _videoPlayer.FindScreen(predicate);
    }

    private float GetReferenceTime()
    {
        if (_activeAudioSource == null || _videoConfig == null) return 0;

        float time;
        if (_activeAudioSource.time == 0)
            time = _lastKnownAudioSourceTime;
        else
            time = _activeAudioSource.time;

        var speed = _videoConfig.PlaybackSpeed;
        return time * speed + _videoConfig.offset / 1000f;
    }

    public VideoConfig? GetVideoConfig()
    {
        return _videoConfig;
    }

    public GameObject? GetVideoPlayerFirstScreen()
    {
        return _videoPlayer.GetFirstScreen();
    }

    public void SetSelectedLevel(BeatmapLevel? level, VideoConfig? config)
    {
        _previewWaitingForPreviewPlayer = true;
        _previewWaitingForVideoPlayer = true;

        _currentLevel = level;
        _videoConfig = config;
        _loggingService.Debug($"Selected Level: {level?.levelID ?? "null"}");

        if (_videoConfig == null)
        {
            _videoPlayer.FadeOut();
            StopAllCoroutines();
            return;
        }

        PrepareVideo(_videoConfig);
        if (level != null && VideoLoader.IsDlcSong(level)) _videoPlayer.FadeOut();
    }

    public void SetVideoPlayerScreenShaderParameters(VideoConfig? config)
    {
        _videoPlayer.SetScreenShaderParameters(config);
    }

    private async Task ShowSongCover()
    {
        if (_currentLevel == null) return;

        try
        {
            var coverSprite = await _currentLevel.previewMediaData.GetCoverSpriteAsync();
            _videoPlayer.SetCoverTexture(coverSprite.texture);
            _videoPlayer.FadeIn();
        }
        catch (Exception e)
        {
            _loggingService.Error(e);
        }
    }

    public void UpdateVideoPlayerPlacement(VideoConfig config, Scene scene)
    {
        _videoPlayer.SetPlacement(
            Placement.CreatePlacementForConfig(config, scene, _videoPlayer.GetVideoAspectRatio()));
    }

    public void SetVideoPlayerSoftParent(Transform transform)
    {
        _videoPlayer.SetSoftParent(transform);
    }

    internal GameObject CreateScreen(Transform parent)
    {
        return _videoPlayer.CreateScreen(parent);
    }
}