using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using BeatSaberTheater.Screen.Interfaces;
using BeatSaberTheater.Util;
using BeatSaberTheater.Video.Config;
using BS_Utils.Utilities;
using UnityEngine;
using UnityEngine.Video;
using Zenject;

namespace BeatSaberTheater.Screen;

public class CustomVideoPlayer : MonoBehaviour
{
    [Inject] private PluginConfig _config = null!;
    [Inject] private ICurvedSurfaceFactory _curvedSurfaceFactory = null!;
    [Inject] private ICustomBloomPrePassFactory _customBloomPrePassFactory = null!;
    [Inject] private EasingHandler _easingHandler = null!;
    [Inject] private LoggingService _loggingService = null!;

    //Initialized by Awake()
    [NonSerialized] private VideoPlayer _player = null!;
    private AudioSource _videoPlayerAudioSource = null!;
    private ScreenManager _screenManager = null!;
    private Renderer _screenRenderer = null!;
    private RenderTexture _renderTexture = null!;

    private const string MAIN_TEXTURE_NAME = "_MainTex";
    private const string THEATER_TEXTURE_NAME = "_CinemaVideoTexture";
    private const string STATUS_PROPERTY_NAME = "_CinemaVideoIsPlaying";
    private const float MAX_BRIGHTNESS = 0.92f;
    private readonly Color _screenColorOn = Color.white.ColorWithAlpha(0f) * MAX_BRIGHTNESS;
    private readonly Color _screenColorOff = Color.clear;
    private static readonly int MainTex = Shader.PropertyToID(MAIN_TEXTURE_NAME);
    private static readonly int TheaterVideoTexture = Shader.PropertyToID(THEATER_TEXTURE_NAME);
    private static readonly int TheaterStatusProperty = Shader.PropertyToID(STATUS_PROPERTY_NAME);
    private string _currentlyPlayingVideo = "";
    private readonly Stopwatch _firstFrameStopwatch = new();

    private const float MAX_VOLUME = 0.28f; //Don't ask, I don't know either.
    [NonSerialized] private float _volumeScale = 1.0f;
    private bool _muted = true;
    private bool _bodyVisible;
    private bool _waitingForFadeOut;

    internal event Action? stopped;
    private event Action<string>? VideoPlayerErrorReceivedEvent;

    public float PanStereo
    {
        set => _videoPlayerAudioSource.panStereo = value;
    }

    public float PlaybackSpeed
    {
        get => _player.playbackSpeed;
        set => _player.playbackSpeed = value;
    }

    public bool PlayerIsPrepared => _player.isPrepared;

    public double PlayerLength => _player.length;

    public double PlayerTime
    {
        get => _player.time;
        set => _player.time = value;
    }

    public float VideoDuration => (float)_player.length;

    public Color ScreenColor
    {
        get => _screenRenderer.material.color;
        set => _screenRenderer.material.color = value;
    }

    public bool VideoEnded { get; private set; }

    public float Volume
    {
        set => _videoPlayerAudioSource.volume = value;
    }

    public string Url
    {
        get => _player.url;
        set => _player.url = value;
    }

    public bool IsPlaying => _player.isPlaying;
    public bool IsFading => _easingHandler.IsFading;
    public bool IsPrepared => _player.isPrepared;
    [NonSerialized] public bool IsSyncing;

    internal void Startup(VideoPlayer.FrameReadyEventHandler frameReadyEventHandler,
        VideoPlayer.EventHandler preparedCompleteEventHandler, Action<string>? videoPlayerErrorReceivedEvent)
    {
        _screenManager = new ScreenManager(_config, _curvedSurfaceFactory, _customBloomPrePassFactory, _loggingService);
        CreateScreen();
        _screenRenderer = _screenManager.GetRenderer();
        _screenRenderer.material = new Material(GetShader().Result) { color = _screenColorOff };
        _screenRenderer.material.enableInstancing = true;

        _player = gameObject.AddComponent<VideoPlayer>();
        _player.source = VideoSource.Url;
        _player.renderMode = VideoRenderMode.RenderTexture;
        _renderTexture = _screenManager.CreateRenderTexture();
        _renderTexture.wrapMode = TextureWrapMode.Mirror;
        _player.targetTexture = _renderTexture;

        _player.playOnAwake = false;
        _player.waitForFirstFrame = true;
        _player.errorReceived += VideoPlayerErrorReceived;
        _player.prepareCompleted += VideoPlayerPrepareComplete;
        _player.started += VideoPlayerStarted;
        _player.loopPointReached += VideoPlayerFinished;

        // TODO: PanStereo does not work as expected with this AudioSource. Panning fully to one side is still slightly audible in the other.
        _videoPlayerAudioSource = gameObject.AddComponent<AudioSource>();
        _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
        _player.SetTargetAudioSource(0, _videoPlayerAudioSource);
        Mute();
        _screenManager.SetScreensActive(false);
        LoopVideo(false);

        _videoPlayerAudioSource.reverbZoneMix = 0f;
        _videoPlayerAudioSource.playOnAwake = false;
        _videoPlayerAudioSource.spatialize = false;

        _screenManager.EnableColorBlending(true);
        _easingHandler.EasingUpdate += FadeHandlerUpdate;
        Hide();

        BSEvents.menuSceneLoaded += OnMenuSceneLoaded;
        SetDefaultMenuPlacement();

        AddFrameReadyEventHandler(frameReadyEventHandler);
        _player.sendFrameReadyEvents = true;
        _player.prepareCompleted += preparedCompleteEventHandler;
        VideoPlayerErrorReceivedEvent += videoPlayerErrorReceivedEvent;
    }

    public void Shutdown(VideoPlayer.FrameReadyEventHandler frameReadyEventHandler,
        VideoPlayer.EventHandler preparedCompleteEventHandler, Action<string>? videoPlayerErrorReceivedEvent)
    {
        RemoveFrameReadyEventHandler(frameReadyEventHandler);
        _player.prepareCompleted -= preparedCompleteEventHandler;
        VideoPlayerErrorReceivedEvent -= videoPlayerErrorReceivedEvent;
    }

    #region Event Handler Binding

    public void AddEasingUpdateEventHandler(Action<float>? eventHandler)
    {
        _easingHandler.EasingUpdate += eventHandler;
    }

    public void RemoveEasingUpdateEventHandler(Action<float>? eventHandler)
    {
        _easingHandler.EasingUpdate -= eventHandler;
    }

    public void AddFrameReadyEventHandler(VideoPlayer.FrameReadyEventHandler eventHandler)
    {
        _player.frameReady += eventHandler;
    }

    public void RemoveFrameReadyEventHandler(VideoPlayer.FrameReadyEventHandler eventHandler)
    {
        _player.frameReady -= eventHandler;
    }

    public void AddPrepareCompletedEventHandler(VideoPlayer.EventHandler eventHandler)
    {
        _player.prepareCompleted += eventHandler;
    }

    #endregion

    public void SetVolumeScale(float volume)
    {
        _volumeScale = volume;
    }

    public void UnloadVideo()
    {
        _player.url = null;
        _player.Prepare();
    }

    #region Unity Event Functions

    public void OnDestroy()
    {
        BSEvents.lateMenuSceneLoadedFresh -= CreateScreenAndPlayer;
        BSEvents.menuSceneLoaded -= OnMenuSceneLoaded;
        _easingHandler.EasingUpdate -= FadeHandlerUpdate;
        _renderTexture.Release();
    }

    #endregion

    private void CreateScreenAndPlayer(ScenesTransitionSetupDataSO? scenesTransition)
    {
    }

    private void CreateScreen()
    {
        _screenManager.CreateScreen(transform);
        _screenManager.SetScreensActive(true);
        SetDefaultMenuPlacement();
    }

    internal GameObject CreateScreen(Transform parent)
    {
        var newScreen = _screenManager.CreateScreen(parent);
        _screenManager.SetScreensActive(true);
        SetDefaultMenuPlacement();
        return newScreen;
    }

    private static async Task<Shader> GetShader(string? path = null)
    {
        AssetBundle myLoadedAssetBundle;
        if (path == null)
        {
            var bundle = await BeatSaberMarkupLanguage.Utilities.GetResourceAsync(Assembly.GetExecutingAssembly(),
                "BeatSaberTheater.Resources.bstheater.bundle");
            if (bundle == null || bundle.Length == 0)
            {
                Plugin._log.Error("GetResource failed");
                return Shader.Find("Hidden/BlitAdd");
            }

            myLoadedAssetBundle = AssetBundle.LoadFromMemory(bundle);
            if (myLoadedAssetBundle == null)
            {
                Plugin._log.Error("LoadFromMemory failed");
                return Shader.Find("Hidden/BlitAdd");
            }
        }
        else
        {
            myLoadedAssetBundle = AssetBundle.LoadFromFile(path);
        }

        var shader = myLoadedAssetBundle.LoadAsset<Shader>("VideoShader");
        myLoadedAssetBundle.Unload(false);

        return shader;
    }

    public void FadeHandlerUpdate(float value)
    {
        ScreenColor = _screenColorOn * value;
        if (!_muted) Volume = MAX_VOLUME * _volumeScale * value;

        if (value >= 1 && _bodyVisible)
            _screenManager.SetScreenBodiesActive(true);
        else
            _screenManager.SetScreenBodiesActive(false);

        if (value == 0 && _player.url == _currentlyPlayingVideo && _waitingForFadeOut) Stop();
    }

    public void OnMenuSceneLoaded()
    {
        SetDefaultMenuPlacement();
    }

    public void SetDefaultMenuPlacement(float? width = null)
    {
        var placement = Placement.MenuPlacement;
        placement.Width = width ?? placement.Height * (21f / 9f);
        SetPlacement(placement);
    }

    public void SetPlacement(Placement placement)
    {
        _screenManager.SetPlacement(placement);
    }

    private void FirstFrameReady(VideoPlayer player, long frame)
    {
        //This is done because the video screen material needs to be set to white, otherwise no video would be visible.
        //When no video is playing, we want it to be black though to not blind the user.
        //If we set the white color when calling Play(), a few frames of white screen are still visible.
        //So, we wait before the player renders its first frame and then set the color, making the switch invisible.
        FadeIn();
        _firstFrameStopwatch.Stop();
        _loggingService.Debug("Delay from Play() to first frame: " + _firstFrameStopwatch.ElapsedMilliseconds + " ms");
        _firstFrameStopwatch.Reset();
        _screenManager.SetAspectRatio(GetVideoAspectRatio());
        _player.frameReady -= FirstFrameReady;
    }

    public void SetBrightness(float brightness)
    {
        _easingHandler.Value = brightness;
    }

    public void SetBloomIntensity(float? bloomIntensity)
    {
        _screenManager.SetBloomIntensity(bloomIntensity);
    }

    internal void LoopVideo(bool loop)
    {
        _player.isLooping = loop;
    }

    public void Show()
    {
        FadeIn(0);
    }

    public void FadeIn(float duration = 0.4f)
    {
        // if (EnvironmentController.IsScreenHidden) return;

        _screenManager.SetScreensActive(true);
        _waitingForFadeOut = false;
        _easingHandler.EaseIn(duration);
    }

    public void Hide()
    {
        FadeOut(0);
    }

    public void FadeOut(float duration = 0.7f)
    {
        _waitingForFadeOut = true;
        _easingHandler.EaseOut(duration);
    }

    public void ShowScreenBody()
    {
        _bodyVisible = true;
        if (!_easingHandler.IsFading && _easingHandler.IsOne) _screenManager.SetScreenBodiesActive(true);
    }

    public void HideScreenBody()
    {
        _bodyVisible = false;
        if (!_easingHandler.IsFading) _screenManager.SetScreenBodiesActive(false);
    }

    public void Play()
    {
        if (_firstFrameStopwatch.IsRunning) return;

        _loggingService.Debug("Starting playback, waiting for first frame...");
        _waitingForFadeOut = false;
        _firstFrameStopwatch.Start();
        _player.frameReady -= FirstFrameReady;
        _player.frameReady += FirstFrameReady;
        _player.Play();
        Shader.SetGlobalInt(TheaterStatusProperty, 1);
    }

    public void Pause()
    {
        _player.Pause();
        _firstFrameStopwatch.Reset();
    }

    public void Stop()
    {
        _loggingService.Debug("Stopping playback");
        _player.Stop();
        stopped?.Invoke();
        SetStaticTexture(null);
        Shader.SetGlobalInt(TheaterStatusProperty, 0);
        _screenManager.SetScreensActive(false);
        _firstFrameStopwatch.Reset();
    }

    public void Prepare()
    {
        stopped?.Invoke();
        _waitingForFadeOut = false;
        _player.Prepare();
    }

    private void Update()
    {
        if (_player.isPlaying || (_player.isPrepared && _player.isPaused)) SetTexture(_player.texture);
    }

    // For manual invocation instead of the event function
    public void UpdateScreenContent()
    {
        SetTexture(_player.texture);
    }

    private void SetTexture(Texture? texture)
    {
        Shader.SetGlobalTexture(TheaterVideoTexture, texture);
    }

    public void SetCoverTexture(Texture? texture)
    {
        SetTexture(texture);

        if (texture == null) return;

        var placement = Placement.CoverPlacement;
        var width = (float)texture.width / texture.height * placement.Height;
        placement.Width = width;
        SetPlacement(placement);
        FadeIn();
    }

    public void SetStaticTexture(Texture? texture)
    {
        if (texture == null)
        {
            ClearTexture();
            return;
        }

        SetTexture(texture);
        var width = (float)texture.width / texture.height * Placement.MenuPlacement.Height;
        SetDefaultMenuPlacement(width);
        _screenManager.SetShaderParameters(null);
    }

    public void ClearTexture()
    {
        var rt = RenderTexture.active;
        RenderTexture.active = _renderTexture;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = rt;
        SetTexture(_renderTexture);
    }

    private static void VideoPlayerPrepareComplete(VideoPlayer source)
    {
        Plugin._log.Debug("Video player prepare complete");
        var texture = source.texture;
        Plugin._log.Debug($"Video resolution: {texture.width}x{texture.height}");
    }

    private void VideoPlayerStarted(VideoPlayer source)
    {
        _loggingService.Debug("Video player started event");
        _currentlyPlayingVideo = source.url;
        _waitingForFadeOut = false;
        VideoEnded = false;
    }

    private void VideoPlayerFinished(VideoPlayer source)
    {
        _loggingService.Debug("Video player loop point event");
        if (!_player.isLooping)
        {
            VideoEnded = true;
            SetBrightness(0f);
        }
    }

    private void VideoPlayerErrorReceived(VideoPlayer source, string message)
    {
        if (message == "Can't play movie []")
            //Expected when preparing null source
            return;

        _loggingService.Error("Video player error: " + message);
        VideoPlayerErrorReceivedEvent?.Invoke(message);
    }

    public float GetVideoAspectRatio()
    {
        var texture = _player.texture;
        if (texture != null && texture.width != 0 && texture.height != 0)
        {
            var aspectRatio = (float)texture.width / texture.height;
            return aspectRatio;
        }

        _loggingService.Debug("Using default aspect ratio (texture missing)");
        return 16f / 9f;
    }

    public void Mute()
    {
        _muted = true;
        Volume = 0f;
    }

    public void Unmute()
    {
        _muted = false;
    }

    public void SetSoftParent(Transform? parent)
    {
        if (_config.Enable360Rotation) _screenManager.SetSoftParent(parent);
    }

    public GameObject? GetFirstScreen()
    {
        return _screenManager?.ScreenGroups[0].Screen;
    }

    public GameObject? FindScreen(Predicate<ScreenObjectGroup> predicate)
    {
        return _screenManager?.ScreenGroups.Find(predicate)?.Screen;
    }

    public void AddScreen(GameObject screen, CurvedSurface curvedSurface, CustomBloomPrePass customBloomPrePass)
    {
        _screenManager.ScreenGroups.Add(new ScreenObjectGroup(screen, curvedSurface, customBloomPrePass));
    }

    public void SetScreenShaderParameters(VideoConfig? config)
    {
        _screenManager.SetShaderParameters(config);
    }

    public void ScreenMenuLoadedFresh()
    {
        _screenManager.OnGameSceneLoadedFresh();
    }

    public void DisableScreen()
    {
        _screenManager.SetScreensActive(false);
    }

    public void ResetScreens()
    {
        _screenManager.ResetScreens();
    }
}