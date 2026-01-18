using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeatmapEditor3D.DataModels;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.GameplaySetup;
using BeatSaberMarkupLanguage.Parser;
using BeatSaberTheater.Download;
using BeatSaberTheater.Playback;
using BeatSaberTheater.Services;
using BeatSaberTheater.Settings;
using BeatSaberTheater.Util;
using BeatSaberTheater.Video;
using BeatSaberTheater.Video.Config;
using HMUI;
using IPA.Utilities;
using JetBrains.Annotations;
using SongCore.Data;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Zenject;
using Object = UnityEngine.Object;

namespace BeatSaberTheater.VideoMenu;

public class VideoMenuUI : IInitializable, IDisposable
{
    private readonly GameplaySetup _gameplaySetup;
    private readonly LoggingService _loggingService;
    private readonly PlaybackManager _playbackManager;
    private readonly PluginConfig _config;

    [UIObject("root-object")] private readonly GameObject _root = null!;
    [UIComponent("no-video-bg")] private readonly RectTransform _noVideoViewRect = null!;
    [UIComponent("video-details")] private readonly RectTransform _videoDetailsViewRect = null!;
    [UIComponent("video-search-results")] private readonly RectTransform _videoSearchResultsViewRect = null!;
    [UIComponent("video-list")] private readonly CustomListTableData _customListTableData = null!;

    [UIComponent("search-results-loading")]
    private readonly TextMeshProUGUI _searchResultsLoadingText = null!;

    [UIComponent("search-keyboard")] private readonly ModalKeyboard _searchKeyboard = null!;
    [UIComponent("video-title")] private readonly TextMeshProUGUI _videoTitleText = null!;
    [UIComponent("no-video-text")] private readonly TextMeshProUGUI _noVideoText = null!;
    [UIComponent("video-author")] private readonly TextMeshProUGUI _videoAuthorText = null!;
    [UIComponent("video-duration")] private readonly TextMeshProUGUI _videoDurationText = null!;
    [UIComponent("video-status")] private readonly TextMeshProUGUI _videoStatusText = null!;
    [UIComponent("video-offset")] private readonly TextMeshProUGUI _videoOffsetText = null!;
    [UIComponent("video-thumbnail")] private readonly Image _videoThumnnail = null!;
    [UIComponent("preview-button")] private readonly TextMeshProUGUI _previewButtonText = null!;
    [UIComponent("preview-button")] private readonly Button _previewButton = null!;
    [UIComponent("search-button")] private readonly Button _searchButton = null!;
    [UIComponent("delete-config-button")] private readonly Button _deleteButton = null!;
    [UIComponent("delete-video-button")] private readonly Button _deleteVideoButton = null!;
    [UIComponent("delete-video-button")] private readonly TextMeshProUGUI _deleteVideoButtonText = null!;
    [UIComponent("download-button")] private readonly Button _downloadButton = null!;
    [UIObject("offset-controls")] private readonly GameObject _offsetControls = null!;
    [UIObject("customize-offset-toggle")] private readonly GameObject _customizeOffsetToggle = null!;
    [UIParams] private readonly BSMLParserParams _bsmlParserParams = null!;

    private Coroutine? _searchLoadingCoroutine;
    private Coroutine? _updateSearchResultsCoroutine;

    [UIValue("customize-offset")]
    public bool CustomizeOffset
    {
        get => _currentVideo != null && (!_currentVideo.IsOfficialConfig ||
                                         _currentVideo.userSettings?.customOffset == true);
        set
        {
            if (_currentVideo == null || !value) return;

            _currentVideo.userSettings ??= new UserSettings();
            _currentVideo.userSettings.customOffset = true;
            _currentVideo.userSettings.originalOffset = _currentVideo.offset;
            _currentVideo.NeedsToSave = true;
            _customizeOffsetToggle.SetActive(false);
            _offsetControls.SetActive(true);
        }
    }

    private VideoMenuStatus _menuStatus = null!;
    private LevelDetailViewController? _levelDetailMenu;
    private bool _videoMenuInitialized;

    private BeatmapLevel? _currentLevel;
    private bool _currentLevelIsPlaylistSong;
    private SongData? _extraSongData;
    private SongData.DifficultyData? _difficultyData;
    private VideoConfig? _currentVideo;
    private bool _videoMenuActive;
    private int _selectedCell;
    private string _searchText = "";

    private string? _thumbnailURL;

    private readonly TheaterCoroutineStarter _coroutineStarter;
    private readonly DownloadService _downloadService;
    private readonly SearchService _searchService;
    private readonly VideoLoader _videoLoader;

    private readonly List<YTResult> _searchResults = [];

    internal VideoMenuUI(TheaterCoroutineStarter coroutineStarter, DownloadService downloadService,
        GameplaySetup gameplaySetup, LoggingService loggingService, PlaybackManager playbackManager,
        PluginConfig config, SearchService searchService, VideoLoader videoLoader)
    {
        _config = config;
        _coroutineStarter = coroutineStarter;
        _downloadService = downloadService;
        _gameplaySetup = gameplaySetup;
        _loggingService = loggingService;
        _playbackManager = playbackManager;
        _searchService = searchService;
        _videoLoader = videoLoader;
    }

    public void Initialize()
    {
        _loggingService.Debug("Adding tab");
        _gameplaySetup.AddTab("Theater", "BeatSaberTheater.VideoMenu.Views.video-menu.bsml", this);

        _loggingService.Debug("Initializing VideoMenu");
        Events.LevelSelected -= OnLevelSelected;
        Events.LevelSelected += OnLevelSelected;
        Events.DifficultySelected -= OnDifficultySelected;
        Events.DifficultySelected += OnDifficultySelected;

        if (_root == null)
        {
            _loggingService.Debug("RootObject is null!");
            return;
        }

        if (_levelDetailMenu != null) _levelDetailMenu.ButtonPressedAction -= OnDeleteVideoAction;

        _levelDetailMenu = new LevelDetailViewController();
        _levelDetailMenu.ButtonPressedAction += OnDeleteVideoAction;
        CreateStatusListener();
        _deleteButton.transform.localScale *= 0.5f;

        _searchKeyboard.ClearOnOpen = false;

        if (_videoMenuInitialized) return;

        _videoMenuInitialized = true;
        _videoDetailsViewRect.gameObject.SetActive(false);
        _videoSearchResultsViewRect.gameObject.SetActive(false);

        _searchService.SearchProgress += SearchProgress;
        _searchService.SearchFinished += SearchFinished;
        _downloadService.DownloadProgress += OnDownloadProgress;
        _downloadService.DownloadFinished += OnDownloadFinished;
        VideoLoader.ConfigChanged += OnConfigChanged;

        if (!_downloadService.LibrariesAvailable())
            _loggingService.Warn(
                $"One or more of the libraries are missing. Downloading videos will not work. To fix this, reinstall Theater and make sure yt-dlp and ffmpeg are in the Libs folder of Beat Saber, which is located at {UnityGame.LibraryPath}.");
    }

    public void Dispose()
    {
        _gameplaySetup.RemoveTab("Theater");
    }

    public void CreateStatusListener()
    {
        //This needs to be reinitialized every time a fresh menu scene load happens
        if (_menuStatus != null)
        {
            _menuStatus.DidEnable -= StatusViewerDidEnable;
            _menuStatus.DidDisable -= StatusViewerDidDisable;
            Object.Destroy(_menuStatus);
        }

        _menuStatus = _root.AddComponent<VideoMenuStatus>();
        _loggingService.Debug("Adding status listener to: " + _menuStatus.name);
        _menuStatus.DidEnable += StatusViewerDidEnable;
        _menuStatus.DidDisable += StatusViewerDidDisable;
    }

    public void ResetVideoMenu()
    {
        _bsmlParserParams.EmitEvent("hide-keyboard");
        _noVideoViewRect.gameObject.SetActive(true);
        _videoDetailsViewRect.gameObject.SetActive(false);
        SetButtonState(false);

        if (!_downloadService.LibrariesAvailable())
        {
            _noVideoText.text =
                "Libraries not found. Please reinstall Theater.\r\nMake sure you unzip the files from the Libs folder into 'Beat Saber\\Libs'.";
            _searchButton.gameObject.SetActive(false);
            return;
        }

        if (!_config.PluginEnabled)
        {
            _noVideoText.text = "Theater is disabled.\r\nYou can re-enable it on the left side of the main menu.";
            return;
        }

        if (_currentLevel == null)
        {
            _noVideoText.text = "No level selected";
            return;
        }

        _noVideoText.text = "No video configured";
    }

    private void OnDownloadProgress(VideoConfig videoConfig)
    {
        UpdateStatusText(videoConfig);
        SetupLevelDetailView(videoConfig);
    }

    public void SetButtonState(bool state)
    {
        _previewButton.interactable = state;
        _deleteButton.interactable = state;
        _deleteVideoButton.interactable = state;
        _searchButton.gameObject.SetActive(_currentLevel != null &&
                                           !VideoLoader.IsDlcSong(_currentLevel) &&
                                           _downloadService.LibrariesAvailable());
        _previewButtonText.text = _playbackManager.IsPreviewPlaying ? "Stop preview" : "Preview";

        if (_currentLevel != null && VideoLoader.IsDlcSong(_currentLevel) && _downloadService.LibrariesAvailable())
            CheckEntitlementAndEnableSearch(_currentLevel).Start();

        if (_currentVideo == null) return;

        //Hide delete config button for mapper-made configs
        var officialConfig = _currentVideo.configByMapper == true;
        _deleteButton.gameObject.SetActive(!officialConfig);

        switch (_currentVideo.DownloadState)
        {
            case DownloadState.Converting:
            case DownloadState.Preparing:
            case DownloadState.Downloading:
            case DownloadState.DownloadingVideo:
            case DownloadState.DownloadingAudio:
                _deleteVideoButtonText.SetText("Cancel");
                _previewButton.interactable = false;
                _deleteVideoButton.transform.Find("Underline").gameObject.GetComponent<Image>().color = Color.grey;
                break;
            case DownloadState.NotDownloaded:
            case DownloadState.Cancelled:
                _deleteVideoButtonText.SetText("Download");
                _deleteVideoButton.interactable = false;
                var underlineColor = Color.clear;
                if (state && _downloadService.LibrariesAvailable())
                {
                    underlineColor = Color.green;
                    _deleteVideoButton.interactable = true;
                }

                _deleteVideoButton.transform.Find("Underline").gameObject.GetComponent<Image>().color = underlineColor;
                _previewButton.interactable = false;
                break;
            default:
                _deleteVideoButtonText.SetText("Delete Video");
                _deleteVideoButton.transform.Find("Underline").gameObject.GetComponent<Image>().color = Color.grey;
                _previewButton.interactable = state;
                break;
        }
    }

    private async Task CheckEntitlementAndEnableSearch(BeatmapLevel level)
    {
        var entitlement = await VideoLoader.GetEntitlementForLevel(level);
        if (entitlement == EntitlementStatus.Owned && _currentLevel == level) _searchButton.gameObject.SetActive(true);
    }

    public void SetupVideoDetails()
    {
        if (!_videoSearchResultsViewRect)
        {
            _loggingService.Warn("Video search results view rect is null, skipping UI setup");
            return;
        }

        _videoSearchResultsViewRect.gameObject.SetActive(false);
        _levelDetailMenu?.SetActive(false);

        if (_currentVideo == null || !_downloadService.LibrariesAvailable())
        {
            ResetVideoMenu();
            _loggingService.Debug("No video configured");
            return;
        }

        // Update download state based on currently selected format
        _currentVideo.UpdateDownloadState(_config.Format);

        SetupLevelDetailView(_currentVideo!);

        //Skip setting up the video menu if it's not showing. Prevents an unnecessary web request for the thumbnail.
        if (!_videoMenuActive)
        {
            ResetVideoMenu();
            _loggingService.Debug("Video Menu is not active");
            return;
        }

        if (_currentVideo!.videoID == null && _currentVideo.videoUrl == null)
        {
            ResetVideoMenu();
            if (_currentVideo.forceEnvironmentModifications != true) return;

            _noVideoText.text =
                "This map uses Theater to modify the environment\r\nwithout displaying a video.\r\n\r\nNo configuration options available.";
            _searchButton.interactable = false;
            _searchButton.gameObject.SetActive(false);

            return;
        }

        _noVideoViewRect.gameObject.SetActive(false);
        _videoDetailsViewRect.gameObject.SetActive(true);

        SetButtonState(true);

        _videoTitleText.text = TheaterFileHelpers.FilterEmoji(_currentVideo.title ?? "Untitled Video");
        _videoAuthorText.text = "Author: " + TheaterFileHelpers.FilterEmoji(_currentVideo.author ?? "Unknown Author");
        _videoDurationText.text = "Duration: " + TheaterFileHelpers.SecondsToString(_currentVideo.duration);

        _videoOffsetText.text = $"{_currentVideo.offset:n0}" + " ms";
        SetThumbnail(_currentVideo.videoID != null
            ? $"https://i.ytimg.com/vi/{_currentVideo.videoID}/hqdefault.jpg"
            : null);

        UpdateStatusText(_currentVideo);
        if (CustomizeOffset)
        {
            _customizeOffsetToggle.SetActive(false);
            _offsetControls.SetActive(true);
        }
        else
        {
            _customizeOffsetToggle.SetActive(true);
            _offsetControls.SetActive(false);
        }

        _bsmlParserParams.EmitEvent("update-customize-offset");
    }

    public void SetupLevelDetailView(VideoConfig videoConfig)
    {
        if (videoConfig != _currentVideo) return;

        // This is the case if the map only uses environment modifications
        if ((_currentVideo.videoID == null && _currentVideo.videoUrl == null) || _levelDetailMenu == null) return;

        switch (videoConfig.DownloadState)
        {
            case DownloadState.Downloaded:
                if (_difficultyData?.HasTheater() == false &&
                    _extraSongData?.HasTheaterInAnyDifficulty() == false)
                {
                    _levelDetailMenu.SetActive(true);
                    _levelDetailMenu.SetText("Please add Theater as a suggestion", null, Color.red);
                }
                else if (videoConfig.ErrorMessage != null)
                {
                    _levelDetailMenu.SetActive(true);
                    _levelDetailMenu.SetText(videoConfig.ErrorMessage, null, Color.red, Color.red);
                }
                else
                {
                    _levelDetailMenu.SetText("Video ready!", null, Color.green);
                }

                break;
            case DownloadState.Preparing:
                _levelDetailMenu.SetActive(true);
                _levelDetailMenu.SetText($"Preparing download...", "Cancel", Color.yellow, Color.red);
                break;
            case DownloadState.Downloading:
                _levelDetailMenu.SetActive(true);
                _levelDetailMenu.SetText(
                    $"Downloading ({Convert.ToInt32(videoConfig.DownloadProgress * 100).ToString()}%)", "Cancel",
                    Color.yellow, Color.red);
                break;
            case DownloadState.DownloadingVideo:
                _levelDetailMenu.SetActive(true);
                _levelDetailMenu.SetText(
                    $"Downloading video ({Convert.ToInt32(videoConfig.DownloadProgress * 100).ToString()}%)", "Cancel",
                    Color.yellow, Color.red);
                break;
            case DownloadState.DownloadingAudio:
                _levelDetailMenu.SetActive(true);
                _levelDetailMenu.SetText(
                    $"Downloading audio ({Convert.ToInt32(videoConfig.DownloadProgress * 100).ToString()}%)", "Cancel",
                    Color.yellow, Color.red);
                break;
            case DownloadState.Converting:
                _levelDetailMenu.SetActive(true);
                _levelDetailMenu.SetText(GetConversionProgressDisplayString(videoConfig),
                    "Cancel", Color.yellow, Color.red);
                break;
            case DownloadState.NotDownloaded:
                _levelDetailMenu.SetActive(true);
                if (videoConfig.ErrorMessage != null)
                    _levelDetailMenu.SetText(videoConfig.ErrorMessage, "Retry", Color.red, Color.red);
                else if (_difficultyData?.HasTheaterRequirement() == true)
                    _levelDetailMenu.SetText("Video required to play this map", "Download", Color.red, Color.green);
                else
                    _levelDetailMenu.SetText("Video available", "Download Video", null, Color.green);

                break;
            case DownloadState.Cancelled:
                _levelDetailMenu.SetActive(true);
                _levelDetailMenu.SetText("Cancelling...", "Download Video", Color.red, Color.green);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void UpdateStatusText(VideoConfig videoConfig)
    {
        if (videoConfig != _currentVideo || !_videoMenuActive) return;

        switch (videoConfig.DownloadState)
        {
            case DownloadState.Downloaded:
                _videoStatusText.text = "Downloaded";
                _videoStatusText.color = Color.green;
                break;
            case DownloadState.Preparing:
                _videoStatusText.text = $"Preparing download...";
                _videoStatusText.color = Color.yellow;
                _previewButton.interactable = false;
                break;
            case DownloadState.Downloading:
                _videoStatusText.text =
                    $"Downloading ({Convert.ToInt32(videoConfig.DownloadProgress * 100).ToString()}%)";
                _videoStatusText.color = Color.yellow;
                _previewButton.interactable = false;
                break;
            case DownloadState.DownloadingVideo:
                _videoStatusText.text =
                    $"Downloading video ({Convert.ToInt32(videoConfig.DownloadProgress * 100).ToString()}%)";
                _videoStatusText.color = Color.yellow;
                _previewButton.interactable = false;
                break;
            case DownloadState.DownloadingAudio:
                _videoStatusText.text =
                    $"Downloading audio ({Convert.ToInt32(videoConfig.DownloadProgress * 100).ToString()}%)";
                _videoStatusText.color = Color.yellow;
                _previewButton.interactable = false;
                break;
            case DownloadState.Converting:
                var convertingText = GetConversionProgressDisplayString(videoConfig);
                _videoStatusText.text = convertingText;
                _videoStatusText.color = Color.yellow;
                _previewButton.interactable = false;
                break;
            case DownloadState.NotDownloaded:
                _videoStatusText.text = videoConfig.ErrorMessage ?? "Not downloaded";
                _videoStatusText.color = Color.red;
                _previewButton.interactable = false;
                break;
            case DownloadState.Cancelled:
                _videoStatusText.text = "Cancelling...";
                _videoStatusText.color = Color.red;
                _previewButton.interactable = false;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void SetThumbnail(string? url)
    {
        if (url != null && url == _thumbnailURL) return;

        _thumbnailURL = url;

        if (url == null)
        {
            SetThumbnailFromCover(_currentLevel).Start();
            return;
        }

        _videoThumnnail.SetImageAsync(url);
    }

    private async Task SetThumbnailFromCover(BeatmapLevel? level)
    {
        if (level == null) return;

        var coverSprite = await level.previewMediaData.GetCoverSpriteAsync();
        _videoThumnnail.sprite = coverSprite;
    }

    public void HandleDidSelectEditorBeatmap(BeatmapDataModel beatmapData, string originalPath)
    {
        if (_config.PluginEnabled) return;

        _playbackManager.StopPreview(true);
        if (_currentVideo?.NeedsToSave == true) _videoLoader.SaveVideoConfig(_currentVideo);

        _currentVideo = _videoLoader.GetConfigForEditorLevel(beatmapData, originalPath);
        _videoLoader.SetupFileSystemWatcher(originalPath);
        _playbackManager.SetSelectedLevel(null, _currentVideo);
    }

    public void HandleDidSelectLevel(BeatmapLevel? level)
    {
        //These will be set a bit later by a Harmony patch. Clear them to not accidentally access outdated info.
        _extraSongData = null;
        _difficultyData = null;

        if (!_config.PluginEnabled ||
            (_currentLevel == level &&
             _currentLevelIsPlaylistSong)) //Ignores the duplicate event that occurs when selecting a playlist song
            return;

        _currentLevelIsPlaylistSong = InstalledMods.BeatSaberPlaylistsLib && level.IsPlaylistLevel();
        if (InstalledMods.BeatSaberPlaylistsLib && _currentLevelIsPlaylistSong)
            level = level.GetLevelFromPlaylistIfAvailable();

        _playbackManager.StopPreview(true);

        if (_currentVideo?.NeedsToSave == true) _videoLoader.SaveVideoConfig(_currentVideo);
        _currentLevel = level;
        if (_currentLevel == null)
        {
            _currentVideo = null;
            _playbackManager.SetSelectedLevel(null, null);
            SetupVideoDetails();
            return;
        }

        _currentVideo = _videoLoader.GetConfigForLevel(_currentLevel);

        _videoLoader.SetupFileSystemWatcher(_currentLevel);
        _playbackManager.SetSelectedLevel(_currentLevel, _currentVideo);
        SetupVideoDetails();

        _searchText = _currentLevel.songName +
                      (!string.IsNullOrEmpty(_currentLevel.songAuthorName) ? " " + _currentLevel.songAuthorName : "");
    }

    private void OnLevelSelected(LevelSelectedArgs levelSelectedArgs)
    {
        if (!_videoMenuInitialized)
        {
            _loggingService.Debug("Initializing video menu (late)");
            Initialize();
        }

        if (levelSelectedArgs.BeatmapData != null)
        {
            _loggingService.Debug("Level selected from VideoMenuUI");
            HandleDidSelectEditorBeatmap(levelSelectedArgs.BeatmapData, levelSelectedArgs.OriginalPath!);
            return;
        }

        HandleDidSelectLevel(levelSelectedArgs.BeatmapLevel);
    }

    private void OnDifficultySelected(ExtraSongDataArgs extraSongDataArgs)
    {
        _extraSongData = extraSongDataArgs.SongData;
        _difficultyData = extraSongDataArgs.SelectedDifficultyData;
        if (_currentVideo != null) SetupLevelDetailView(_currentVideo);
    }

    public void OnConfigChanged(VideoConfig? config)
    {
        _currentVideo = config;
        SetupVideoDetails();
    }

    public void StatusViewerDidEnable(object sender, EventArgs e)
    {
        _videoMenuActive = true;
        _playbackManager.StopPreview(false);
        SetupVideoDetails();
    }

    public void StatusViewerDidDisable(object sender, EventArgs e)
    {
        _videoMenuActive = false;
        if (_currentVideo?.NeedsToSave == true) _videoLoader.SaveVideoConfig(_currentVideo);

        _searchService.StopSearch();

        try
        {
            _playbackManager.StopPreview(true);
        }
        catch (Exception exception)
        {
            //This can happen when closing the game
            _loggingService.Debug(exception);
        }
    }

    private void ApplyOffset(int offset)
    {
        if (_currentVideo == null) return;

        _currentVideo.offset += offset;
        _videoOffsetText.text = $"{_currentVideo.offset:n0}" + " ms";
        _currentVideo.NeedsToSave = true;
        _playbackManager.ApplyOffset(offset);
    }

    [UIAction("on-search-action")]
    [UsedImplicitly]
    public void SearchAction()
    {
        if (_currentLevel == null)
        {
            _loggingService.Warn("Selected level was null on search action");
            return;
        }

        OnQueryAction(_searchText);
        _customListTableData.TableView.ScrollToCellWithIdx(0, TableView.ScrollPositionType.Beginning, false);
        _customListTableData.TableView.ClearSelection();
    }

    private IEnumerator UpdateSearchResults(YTResult result)
    {
        var title =
            $"[{TheaterFileHelpers.SecondsToString(result.Duration)}] {TheaterFileHelpers.FilterEmoji(result.Title)}";
        var description = $"{TheaterFileHelpers.FilterEmoji(result.Author)}";

        try
        {
            var stillImage = result.IsStillImage();
            string descriptionAddition;
            if (stillImage)
                descriptionAddition = "Likely a still image";
            else
                descriptionAddition = result.GetQualityString() ?? "";

            if (descriptionAddition.Length > 0) description += "   |   " + descriptionAddition;
        }
        catch (Exception e)
        {
            _loggingService.Warn(e);
        }

        var item = new CustomListTableData.CustomCellInfo(title, description);
        var request = UnityWebRequestTexture.GetTexture($"https://i.ytimg.com/vi/{result.ID}/mqdefault.jpg");
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.ConnectionError &&
            request.result != UnityWebRequest.Result.ProtocolError)
        {
            var tex = ((DownloadHandlerTexture)request.downloadHandler).texture;
            item.Icon = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f, 100, 1);
        }
        else
        {
            _loggingService.Debug(request.error);
        }

        _customListTableData.Data.Add(item);
        _customListTableData.TableView.ReloadDataKeepingPosition();

        _downloadButton.interactable = _selectedCell != -1;
        _downloadButton.transform.Find("Underline").gameObject.GetComponent<Image>().color = Color.green;
        _searchResultsLoadingText.gameObject.SetActive(false);
    }

    private void OnDownloadFinished(VideoConfig video)
    {
        if (_currentVideo != video) return;

        if (video.ErrorMessage != null)
        {
            SetupVideoDetails();
            return;
        }

        // Update state to reflect the currently selected format
        video.UpdateDownloadState(_config.Format);

        _playbackManager.PrepareVideo(video);

        if (_currentLevel != null) _videoLoader.RemoveConfigFromCache(_currentLevel);

        SetupVideoDetails();
        _levelDetailMenu?.SetActive(true);
        _levelDetailMenu?.RefreshContent();
    }

    public void ShowKeyboard()
    {
        _searchKeyboard.SetText(_searchText);
        _bsmlParserParams.EmitEvent("show-keyboard");
    }

    [UIAction("on-refine-action")]
    [UsedImplicitly]
    private void OnRefineAction()
    {
        ShowKeyboard();
    }

    [UIAction("on-delete-video-action")]
    [UsedImplicitly]
    private void OnDeleteVideoAction()
    {
        if (_currentVideo == null)
        {
            _loggingService.Warn("Current video was null on delete action");
            return;
        }

        _playbackManager.StopPreview(true);

        switch (_currentVideo.DownloadState)
        {
            case DownloadState.Preparing:
            case DownloadState.Downloading:
            case DownloadState.DownloadingAudio:
            case DownloadState.DownloadingVideo:
                _downloadService.CancelDownload(_currentVideo);
                break;
            case DownloadState.NotDownloaded:
            case DownloadState.Cancelled:
                _currentVideo.DownloadProgress = 0;
                _searchService.StopSearch();
                _downloadService.StartDownload(_currentVideo, _config.QualityMode, _config.Format);
                _currentVideo.NeedsToSave = true;
                _videoLoader.AddConfigToCache(_currentVideo, _currentLevel!);
                break;
            default:
                _videoLoader.DeleteVideo(_currentVideo, _config.Format);
                _playbackManager.StopAndUnloadVideo();
                SetupLevelDetailView(_currentVideo);
                _levelDetailMenu?.RefreshContent();
                break;
        }

        UpdateStatusText(_currentVideo);
        SetButtonState(true);
    }

    [UIAction("on-delete-config-action")]
    [UsedImplicitly]
    private void OnDeleteConfigAction()
    {
        if (_currentVideo == null || _currentLevel == null)
        {
            _loggingService.Warn("Failed to delete config: Either currentVideo or currentLevel is null");
            return;
        }

        _playbackManager.StopPreview(true);
        _playbackManager.StopPlayback();
        _playbackManager.HideVideoPlayer();

        if (_currentVideo.IsDownloading) _downloadService.CancelDownload(_currentVideo);

        // Delete all downloaded formats when user explicitly deletes the video
        foreach (VideoFormats.Format format in Enum.GetValues(typeof(VideoFormats.Format)))
        {
            _videoLoader.DeleteVideo(_currentVideo, format);
        }
        var success = _videoLoader.DeleteConfig(_currentVideo, _currentLevel);
        if (success) _currentVideo = null;

        _levelDetailMenu?.SetActive(false);
        ResetVideoMenu();
    }

    [UIAction("on-back-action")]
    [UsedImplicitly]
    private void OnBackAction()
    {
        _videoDetailsViewRect.gameObject.SetActive(true);
        _videoSearchResultsViewRect.gameObject.SetActive(false);
        SetupVideoDetails();
    }

    [UIAction("on-query")]
    [UsedImplicitly]
    private void OnQueryAction(string query)
    {
        _noVideoViewRect.gameObject.SetActive(false);
        _videoDetailsViewRect.gameObject.SetActive(false);
        _videoSearchResultsViewRect.gameObject.SetActive(true);

        ResetSearchView();
        _downloadButton.interactable = false;
        _searchLoadingCoroutine = _coroutineStarter.StartCoroutine(SearchLoadingCoroutine());

        _searchService.Search(query);
        _searchText = query;
    }

    private void SearchProgress(YTResult result)
    {
        //Event is being invoked twice for whatever reason, so keep a list of what has been added before
        if (_searchResults.Contains(result)) return;

        _searchResults.Add(result);
        var updateSearchResultsCoroutine = UpdateSearchResults(result);
        _updateSearchResultsCoroutine = _coroutineStarter.StartCoroutine(updateSearchResultsCoroutine);
    }

    private void SearchFinished()
    {
        if (_searchResults.Count != 0) return;

        ResetSearchView();
        _searchResultsLoadingText.gameObject.SetActive(true);
        _searchResultsLoadingText.SetText(
            "No search results found.\r\nUse the Refine Search button in the bottom right to choose a different search query.");
    }

    private void ResetSearchView()
    {
        if (_searchLoadingCoroutine != null) _coroutineStarter.StopCoroutine(_searchLoadingCoroutine);
        if (_updateSearchResultsCoroutine != null)
            _coroutineStarter.StopCoroutine(_updateSearchResultsCoroutine);

        if (_customListTableData.Data != null && _customListTableData.Data.Count > 0)
        {
            _customListTableData.Data.Clear();
            _customListTableData.TableView.ReloadData();
        }

        _downloadButton.interactable = false;
        _downloadButton.transform.Find("Underline").gameObject.GetComponent<Image>().color = Color.grey;
        _selectedCell = -1;
        _searchResults.Clear();
        _bsmlParserParams.EmitEvent("hide-keyboard");
    }

    private IEnumerator SearchLoadingCoroutine()
    {
        var count = 0;
        const string loadingText = "Searching for videos, please wait";
        _searchResultsLoadingText.gameObject.SetActive(true);

        //Loading animation
        while (_searchResultsLoadingText.gameObject.activeInHierarchy)
        {
            var periods = string.Empty;
            count++;

            for (var i = 0; i < count; i++) periods += ".";

            if (count == 3) count = 0;

            _searchResultsLoadingText.SetText(loadingText + periods);

            yield return new WaitForSeconds(0.5f);
        }
    }

    [UIAction("on-select-cell")]
    [UsedImplicitly]
    private void OnSelectCell(TableView view, int selection)
    {
        if (_customListTableData.Data.Count > selection)
        {
            _selectedCell = selection;
            _downloadButton.interactable = true;
        }
        else
        {
            _downloadButton.interactable = false;
            _selectedCell = -1;
        }
    }

    [UIAction("on-download-action")]
    [UsedImplicitly]
    private void OnDownloadAction()
    {
        _loggingService.Debug("Download pressed");
        if (_selectedCell < 0 || _currentLevel == null)
        {
            _loggingService.Error("No cell or level selected on download action");
            return;
        }

        _downloadButton.interactable = false;
        var config =
            new VideoConfig(_searchService.SearchResults[_selectedCell], VideoLoader.GetTheaterLevelPath(_currentLevel))
            { NeedsToSave = true };
        _videoLoader.AddConfigToCache(config, _currentLevel);
        _searchService.StopSearch();
        _downloadService.StartDownload(config, _config.QualityMode, _config.Format);
        _currentVideo = config;
        SetupVideoDetails();
    }

    [UIAction("on-preview-action")]
    [UsedImplicitly]
    private void OnPreviewAction()
    {
        _playbackManager.StartPreview().Start();
        SetButtonState(true);
    }

    [UIAction("on-offset-decrease-action-high")]
    [UsedImplicitly]
    private void DecreaseOffsetHigh()
    {
        ApplyOffset(-1000);
    }

    [UIAction("on-offset-decrease-action-mid")]
    [UsedImplicitly]
    private void DecreaseOffsetMid()
    {
        ApplyOffset(-100);
    }

    [UIAction("on-offset-decrease-action-low")]
    [UsedImplicitly]
    private void DecreaseOffsetLow()
    {
        ApplyOffset(-20);
    }

    [UIAction("on-offset-increase-action-high")]
    [UsedImplicitly]
    private void IncreaseOffsetHigh()
    {
        ApplyOffset(1000);
    }

    [UIAction("on-offset-increase-action-mid")]
    [UsedImplicitly]
    private void IncreaseOffsetMid()
    {
        ApplyOffset(100);
    }

    [UIAction("on-offset-increase-action-low")]
    [UsedImplicitly]
    private void IncreaseOffsetLow()
    {
        ApplyOffset(20);
    }

    private string GetConversionProgressDisplayString(VideoConfig config)
    {
        var convertingText = config.ConvertingProgress.HasValue
            ? $"Converting ({config.ConvertingProgress:##}%)"
            : "Converting...";

        return convertingText;
    }
}

public class VideoMenuStatus : MonoBehaviour
{
    public event EventHandler DidEnable = null!;
    public event EventHandler DidDisable = null!;

    private void OnEnable()
    {
        var handler = DidEnable;

        handler.Invoke(this, EventArgs.Empty);
    }

    private void OnDisable()
    {
        var handler = DidDisable;

        handler.Invoke(this, EventArgs.Empty);
    }
}