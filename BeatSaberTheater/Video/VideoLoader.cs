using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeatmapEditor3D.DataModels;
using BeatSaberTheater.Download;
using BeatSaberTheater.Util;
using BeatSaberTheater.Video.Config;
using IPA.Utilities;
using IPA.Utilities.Async;
using Newtonsoft.Json;
using SongCore;
using UnityEngine;
using Zenject;

namespace BeatSaberTheater.Video;

internal class VideoLoader(
    PluginConfig _config,
    TheaterCoroutineStarter _coroutineStarter,
    LoggingService _loggingService,
    CustomLevelLoader _customLevelLoader)
    : IInitializable, IDisposable
{
    private const string LEGACY_OST_DIRECTORY_NAME = "CinemaOSTVideos";

    private const string OST_DIRECTORY_NAME = "TheaterOSTVideos";

    // internal const string LEGACY_WIP_DIRECTORY_NAME = "TheaterWIPVideos";
    // internal const string WIP_DIRECTORY_NAME = "TheaterWIPVideos";
    internal const string WIP_MAPS_FOLDER = "CustomWIPLevels";
    private const string LEGACY_CONFIG_FILENAME = "cinema-video.json";
    private const string CONFIG_FILENAME = "theater-video.json";

    private static FileSystemWatcher? _fileSystemWatcher;
    public static event Action<VideoConfig?>? ConfigChanged;
    private static string? _ignoreNextEventForPath;

    //This should ideally be a HashSet, but there is no concurrent version of it. We also don't need the value, so use the smallest possible type.
    private static readonly ConcurrentDictionary<string, byte> MapsWithVideo = new();
    private static readonly ConcurrentDictionary<string, VideoConfig> CachedConfigs = new();
    private static readonly ConcurrentDictionary<string, VideoConfig> BundledConfigs = new();

    private static BeatmapLevelsModel? _beatmapLevelsModel;

    public static BeatmapLevelsModel BeatmapLevelsModel
    {
        get
        {
            if (_beatmapLevelsModel == null) _beatmapLevelsModel = Plugin._menuContainer.Resolve<BeatmapLevelsModel>();

            return _beatmapLevelsModel;
        }
    }

    private static BeatmapLevelsEntitlementModel? _beatmapLevelsEntitlementModel;

    private static BeatmapLevelsEntitlementModel BeatmapLevelsEntitlementModel
    {
        get
        {
            if (_beatmapLevelsEntitlementModel == null)
                _beatmapLevelsEntitlementModel = BeatmapLevelsModel._entitlements;

            return _beatmapLevelsEntitlementModel;
        }
    }

    private static AudioClipAsyncLoader AudioClipAsyncLoader
    {
        get
        {
            if (_audioClipAsyncLoader == null)
                _audioClipAsyncLoader = Plugin._menuContainer.Resolve<AudioClipAsyncLoader>();

            return _audioClipAsyncLoader;
        }
    }

    private static AudioClipAsyncLoader? _audioClipAsyncLoader;

    internal static void IndexMaps(Loader? loader,
        ConcurrentDictionary<string, BeatmapLevel>? beatmapLevels)
    {
        Task.Run(IndexMapsAsync);
    }

    private static async Task IndexMapsAsync()
    {
        Plugin._log.Debug("Indexing maps...");
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var officialMaps = GetOfficialMaps();

        void Action()
        {
            var options = new ParallelOptions
            { MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount / 2 - 1) };
            Parallel.ForEach(Loader.CustomLevels, options, IndexMap);
            if (officialMaps.Count > 0) Parallel.ForEach(officialMaps, options, IndexMap);
        }

        var loadingTask = new Task(Action, CancellationToken.None);
        var loadingAwaiter = loadingTask.ConfigureAwait(false);
        loadingTask.Start();
        await loadingAwaiter;

        Plugin._log.Debug($"Indexing took {stopwatch.ElapsedMilliseconds} ms");
    }

    private static List<BeatmapLevel> GetOfficialMaps()
    {
        var officialMaps = new List<BeatmapLevel>();

        void AddOfficialPackCollection(BeatmapLevelsRepository beatmapLevelsRepository)
        {
            officialMaps.AddRange(beatmapLevelsRepository.beatmapLevelPacks.SelectMany(pack => pack._beatmapLevels));
        }

        AddOfficialPackCollection(BeatmapLevelsModel.ostAndExtrasBeatmapLevelsRepository);
        AddOfficialPackCollection(BeatmapLevelsModel.dlcBeatmapLevelsRepository);

        return officialMaps;
    }

    private static void IndexMap(KeyValuePair<string, BeatmapLevel> levelKeyValuePair)
    {
        IndexMap(levelKeyValuePair.Value);
    }

    private static void IndexMap(BeatmapLevel level)
    {
        var levelPath = GetTheaterLevelPath(level);
        var configPath = GetConfigPath(levelPath);
        if (File.Exists(configPath)) MapsWithVideo.TryAdd(level.levelID, 0);
    }

    public static string GetConfigPath(string levelPath)
    {
        var legacyConfigPath = Path.Combine(levelPath, LEGACY_CONFIG_FILENAME);
        if (File.Exists(legacyConfigPath)) return legacyConfigPath;

        return Path.Combine(levelPath, CONFIG_FILENAME);
    }

    public void AddConfigToCache(VideoConfig config, BeatmapLevel level)
    {
        var success = CachedConfigs.TryAdd(level.levelID, config);
        MapsWithVideo.TryAdd(level.levelID, 0);
        if (success) _loggingService.Debug($"Adding config for {level.levelID} to cache");
    }

    public void RemoveConfigFromCache(BeatmapLevel level)
    {
        var success = CachedConfigs.TryRemove(level.levelID, out _);
        if (success) _loggingService.Debug($"Removing config for {level.levelID} from cache");
    }

    private VideoConfig? GetConfigFromCache(BeatmapLevel level)
    {
        var success = CachedConfigs.TryGetValue(level.levelID, out var config);
        if (success) _loggingService.Debug($"Loading config for {level.levelID} from cache");
        return config;
    }

    private VideoConfig? GetConfigFromBundledConfigs(BeatmapLevel level)
    {
        var levelID = !level.hasPrecalculatedData
            ? level.levelID
            : TheaterFileHelpers.ReplaceIllegalFilesystemChars(level.songName.Trim());
        BundledConfigs.TryGetValue(levelID, out var config);

        if (config == null)
        {
            _loggingService.Debug($"No bundled config found for {levelID}");
            return null;
        }

        config.LevelDir = GetTheaterLevelPath(level);
        config.bundledConfig = true;
        _loggingService.Debug("Loaded from bundled configs");
        return config;
    }

    public static void StopFileSystemWatcher()
    {
        Plugin._log.Debug("Disposing FileSystemWatcher");
        _fileSystemWatcher?.Dispose();
    }

    public void SetupFileSystemWatcher(BeatmapLevel level)
    {
        var levelPath = GetTheaterLevelPath(level);
        ListenForConfigChanges(levelPath);
    }

    public void SetupFileSystemWatcher(string path)
    {
        ListenForConfigChanges(path);
    }

    private void ListenForConfigChanges(string levelPath)
    {
        _fileSystemWatcher?.Dispose();
        if (!Directory.Exists(levelPath))
        {
            if (File.Exists(levelPath))
            {
                levelPath = Path.GetDirectoryName(levelPath)!;
            }
            else
            {
                _loggingService.Debug($"Level directory {levelPath} does not exist");
                return;
            }
        }

        _loggingService.Debug($"Setting up FileSystemWatcher for {levelPath}");

        _fileSystemWatcher = new FileSystemWatcher();
        var configPath = GetConfigPath(levelPath);
        _fileSystemWatcher.Path = Path.GetDirectoryName(configPath);
        _fileSystemWatcher.Filter = Path.GetFileName(configPath);
        _fileSystemWatcher.EnableRaisingEvents = true;

        _fileSystemWatcher.Changed += OnConfigChanged;
        _fileSystemWatcher.Created += OnConfigChanged;
        _fileSystemWatcher.Deleted += OnConfigChanged;
        _fileSystemWatcher.Renamed += OnConfigChanged;
    }

    private void OnConfigChanged(object _, FileSystemEventArgs e)
    {
        UnityMainThreadTaskScheduler.Factory.StartNew(delegate { OnConfigChangedMainThread(e); });
    }

    private void OnConfigChangedMainThread(FileSystemEventArgs e)
    {
        _loggingService.Debug("Config " + e.ChangeType + " detected: " + e.FullPath);
        if (_ignoreNextEventForPath == e.FullPath && !TheaterFileHelpers.IsInEditor())
        {
            _loggingService.Debug("Ignoring event after saving");
            _ignoreNextEventForPath = null;
            return;
        }

        _coroutineStarter.StartCoroutine(WaitForConfigWriteCoroutine(e));
    }

    private IEnumerator WaitForConfigWriteCoroutine(FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Deleted)
        {
            ConfigChanged?.Invoke(null);
            yield break;
        }

        var configPath = e.FullPath;
        var configFileInfo = new FileInfo(configPath);
        var timeout = new DownloadTimeout(3f);
        yield return new WaitUntil(() =>
            !TheaterFileHelpers.IsFileLocked(configFileInfo) || timeout.HasTimedOut);
        var config = LoadConfig(configPath);
        ConfigChanged?.Invoke(config);
    }

    public static bool IsDlcSong(BeatmapLevel level)
    {
        return level.GetType() == typeof(BeatmapLevelSO);
    }

    public static async Task<AudioClip?> GetAudioClipForLevel(BeatmapLevel level)
    {
        if (!IsDlcSong(level)) return await LoadAudioClipAsync(level);

        var beatmapLevelLoader = (BeatmapLevelLoader)BeatmapLevelsModel.levelLoader;
        if (beatmapLevelLoader._loadedBeatmapLevelDataCache.TryGetFromCache(level.levelID, out var beatmapLevelData))
        {
            Plugin._log.Debug("Getting audio clip from async cache");
            return await _audioClipAsyncLoader.LoadSong(beatmapLevelData);
        }

        return await LoadAudioClipAsync(level);
    }

    private static async Task<AudioClip?> LoadAudioClipAsync(BeatmapLevel level)
    {
        var loaderTask = AudioClipAsyncLoader?.LoadPreview(level);
        if (loaderTask == null)
        {
            Plugin._log.Error("AudioClipAsyncLoader.LoadPreview() failed");
            return null;
        }

        return await loaderTask;
    }

    public static async Task<EntitlementStatus> GetEntitlementForLevel(BeatmapLevel level)
    {
        return await BeatmapLevelsEntitlementModel.GetLevelEntitlementStatusAsync(level.levelID,
            CancellationToken.None);
    }

    public VideoConfig? GetConfigForEditorLevel(BeatmapDataModel _, string originalPath)
    {
        if (!Directory.Exists(originalPath))
        {
            _loggingService.Debug($"Path does not exist: {originalPath}");
            return null;
        }

        var configPath = GetConfigPath(originalPath);
        var videoConfig = LoadConfig(configPath);

        return videoConfig;
    }

    public static bool LevelHasVideo(BeatmapLevel level)
    {
        return MapsWithVideo.ContainsKey(level.levelID);
    }

    public VideoConfig? GetConfigForLevel(BeatmapLevel? level)
    {
        if (InstalledMods.BeatSaberPlaylistsLib) level = level.GetLevelFromPlaylistIfAvailable();

        if (level == null) return null;

        var cachedConfig = GetConfigFromCache(level);
        if (cachedConfig != null)
        {
            if (cachedConfig.DownloadState == DownloadState.Downloaded) RemoveConfigFromCache(level);
            return cachedConfig;
        }

        VideoConfig? videoConfig = null;
        var levelPath = GetTheaterLevelPath(level);
        if (Directory.Exists(levelPath))
            videoConfig = LoadConfig(GetConfigPath(levelPath));
        else
            _loggingService.Debug($"Path does not exist: {levelPath}");

        // Check the song folder for the video config
        if (videoConfig == null)
        {
            var mapPath = GetMapPath(level);
            videoConfig = LoadConfig(GetConfigPath(mapPath));
        }

        if (InstalledMods.BeatSaberPlaylistsLib && videoConfig == null &&
            level.TryGetPlaylistLevelConfig(levelPath, _config.Format, out var playlistConfig)) videoConfig = playlistConfig;

        return videoConfig ?? GetConfigFromBundledConfigs(level);
    }

    private string GetMapPath(BeatmapLevel level)
    {
        _customLevelLoader._loadedBeatmapSaveData.TryGetValue(level.levelID, out var loadedSaveData);
        var mapPath = loadedSaveData.customLevelFolderInfo.folderPath;
        _loggingService.Debug($"Found map: {mapPath}");

        return mapPath;
    }

    public static string GetTheaterLevelPath(BeatmapLevel level)
    {
        var songName = level.songName.Trim();
        songName = TheaterFileHelpers.ReplaceIllegalFilesystemChars(songName);
        var songFolder = $"{songName} - {level.levelID}";
        var levelPath = Path.Combine(UnityGame.InstallPath, "Beat Saber_Data", "CustomLevels",
            OST_DIRECTORY_NAME,
            songFolder);

        // Check Cinema folder if Theater config doesn't exist
        if (!Directory.Exists(levelPath))
        {
            var legacyLevelPath = Path.Combine(UnityGame.InstallPath, "Beat Saber_Data", "CustomLevels",
                LEGACY_OST_DIRECTORY_NAME,
                songName);
            if (Directory.Exists(legacyLevelPath))
                levelPath = legacyLevelPath;
        }

        return levelPath;
    }

    public void SaveVideoConfig(VideoConfig videoConfig)
    {
        if (videoConfig.LevelDir == null || videoConfig.ConfigPath == null || !Directory.Exists(videoConfig.LevelDir))
        {
            _loggingService.Warn("Failed to save video. Path " + videoConfig.LevelDir + " does not exist.");
            return;
        }

        if (videoConfig.IsWIPLevel) videoConfig.configByMapper = true;

        var configPath = videoConfig.ConfigPath;
        SaveVideoConfigToPath(videoConfig, configPath);
    }

    private void SaveVideoConfigToPath(VideoConfig config, string configPath)
    {
        _ignoreNextEventForPath = configPath;
        _loggingService.Info($"Saving video config to {configPath}");

        try
        {
            File.WriteAllText(configPath,
                JsonConvert.SerializeObject(config, Formatting.Indented));
            config.NeedsToSave = false;
        }
        catch (Exception e)
        {
            _loggingService.Error("Failed to save level data: ");
            _loggingService.Error(e);
        }

        if (!File.Exists(configPath))
            _loggingService.Error("Config file doesn't exist after saving!");
        else
            _loggingService.Debug("Config save successful");
    }

    public void DeleteVideo(VideoConfig videoConfig)
    {
        try
        {
            // Delete all downloaded video files
            foreach (var kvp in videoConfig.DownloadedFormats)
            {
                if (File.Exists(kvp.Value))
                {
                    File.Delete(kvp.Value);
                    _loggingService.Info("Deleted video at " + kvp.Value);
                }
            }

            // Clear the DownloadedFormats dictionary
            videoConfig.DownloadedFormats.Clear();

            // Update download state
            if (videoConfig.DownloadState != DownloadState.Cancelled)
                videoConfig.DownloadState = DownloadState.NotDownloaded;

            // Clear the legacy videoFile property
            videoConfig.videoFile = null;
        }
        catch (Exception e)
        {
            _loggingService.Error("Failed to delete video files");
            _loggingService.Error(e);
        }
    }

    public bool DeleteConfig(VideoConfig videoConfig, BeatmapLevel level)
    {
        if (videoConfig.LevelDir is null)
        {
            _loggingService.Error("LevelDir was null when trying to delete config");
            return false;
        }

        try
        {
            var theaterConfigPath = GetConfigPath(videoConfig.LevelDir);
            if (File.Exists(theaterConfigPath)) File.Delete(theaterConfigPath);

            MapsWithVideo.TryRemove(level.levelID, out _);
        }
        catch (Exception e)
        {
            _loggingService.Error("Failed to delete video config:");
            _loggingService.Error(e);
        }

        RemoveConfigFromCache(level);
        _loggingService.Info("Deleted video config");

        return true;
    }

    private VideoConfig? LoadConfig(string configPath)
    {
        if (!File.Exists(configPath)) return null;

        VideoConfig? videoConfig;
        try
        {
            _loggingService.Debug($"Config path: {configPath}");
            var json = File.ReadAllText(configPath);
            _loggingService.Debug($"Config json: {json}");
            videoConfig = JsonConvert.DeserializeObject<VideoConfig>(json);
        }
        catch (Exception e)
        {
            _loggingService.Error($"Error parsing video json {configPath}:");
            _loggingService.Error(e);
            return null;
        }

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (videoConfig != null)
        {
            videoConfig.LevelDir = Path.GetDirectoryName(configPath);
            // Update download state with the current format from config
            videoConfig.UpdateDownloadState(_config.Format);
        }
        else
        {
            _loggingService.Warn($"Deserializing video config at {configPath} failed");
        }

        return videoConfig;
    }

    private async Task<IEnumerable<BundledConfig>> LoadBundledConfigs()
    {
        var buffer = await BeatSaberMarkupLanguage.Utilities.GetResourceAsync(Assembly.GetExecutingAssembly(),
            "BeatSaberTheater.Resources.configs.json");
        var jsonString = Encoding.UTF8.GetString(buffer, 0, buffer.Length);
        var configs = JsonConvert.DeserializeObject<BundledConfig[]>(jsonString);
        if (configs == null)
        {
            _loggingService.Error("Failed to deserialize bundled configs");
            configs = [];
        }

        return configs;
    }

    private async Task MigrateOSTVideosAsync()
    {
        try
        {
            var customLevelsBasePath = Path.Combine(UnityGame.InstallPath, "Beat Saber_Data", "CustomLevels");
            var legacyBasePath = Path.Combine(customLevelsBasePath, LEGACY_OST_DIRECTORY_NAME);
            var newBasePath = Path.Combine(customLevelsBasePath, OST_DIRECTORY_NAME);

            // Move all videos from legacy folder to non-legacy folder
            if (Directory.Exists(legacyBasePath))
            {
                _loggingService.Info($"Starting migration: moving legacy {LEGACY_OST_DIRECTORY_NAME} to {OST_DIRECTORY_NAME}");

                if (!Directory.Exists(newBasePath))
                    Directory.CreateDirectory(newBasePath);

                var legacyDirs = Directory.GetDirectories(legacyBasePath);
                foreach (var legacyDir in legacyDirs)
                {
                    try
                    {
                        var songFolderName = Path.GetFileName(legacyDir);
                        var targetPath = Path.Combine(newBasePath, songFolderName);

                        // If target already exists, skip
                        if (Directory.Exists(targetPath))
                        {
                            _loggingService.Debug($"Target already exists, skipping: {songFolderName}");
                            continue;
                        }

                        Directory.Move(legacyDir, targetPath);
                        _loggingService.Info($"Moved {songFolderName} from legacy to non-legacy directory");

                        // Rename legacy config file to new config file
                        var legacyConfigPath = Path.Combine(targetPath, LEGACY_CONFIG_FILENAME);
                        if (File.Exists(legacyConfigPath))
                        {
                            var newConfigPath = Path.Combine(targetPath, CONFIG_FILENAME);
                            if (!File.Exists(newConfigPath))
                            {
                                File.Move(legacyConfigPath, newConfigPath);
                                _loggingService.Info($"Renamed {LEGACY_CONFIG_FILENAME} to {CONFIG_FILENAME} in {songFolderName}");
                            }
                            else
                            {
                                File.Delete(legacyConfigPath);
                                _loggingService.Debug($"New config already exists, deleted legacy {LEGACY_CONFIG_FILENAME} in {songFolderName}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _loggingService.Warn($"Failed to move legacy folder {legacyDir}:");
                        _loggingService.Warn(e);
                    }
                }

                _loggingService.Info("Legacy videos moved to non-legacy directory");
            }
        }
        catch (Exception e)
        {
            _loggingService.Error("Error during OST video migration:");
            _loggingService.Error(e);
        }
    }

    private async Task RenameSongFolders()
    {
        try
        {
            // Rename all folders in non-legacy directory to new format (songName - levelID)
            var ostBasePath = Path.Combine(UnityGame.InstallPath, "Beat Saber_Data", "CustomLevels", OST_DIRECTORY_NAME);
            _loggingService.Info("Starting migration: converting folder names to new format");

            if (!Directory.Exists(ostBasePath))
            {
                _loggingService.Debug($"Non-legacy OST directory does not exist: {ostBasePath}");
                return;
            }

            // Build a map of song names to list of beatmap levels (for duplicates)
            var songNameToLevels = new Dictionary<string, List<BeatmapLevel>>(StringComparer.OrdinalIgnoreCase);
            var levelIdToLevel = new Dictionary<string, BeatmapLevel>();

            // Add all official maps
            var officialMaps = GetOfficialMaps();
            foreach (var level in officialMaps)
            {
                var songName = TheaterFileHelpers.ReplaceIllegalFilesystemChars(level.songName.Trim());
                if (!songNameToLevels.ContainsKey(songName))
                    songNameToLevels[songName] = new List<BeatmapLevel>();
                songNameToLevels[songName].Add(level);
                levelIdToLevel[level.levelID] = level;
            }

            // Add all custom levels
            foreach (var levelPair in Loader.CustomLevels)
            {
                var level = levelPair.Value;
                var songName = TheaterFileHelpers.ReplaceIllegalFilesystemChars(level.songName.Trim());
                if (!songNameToLevels.ContainsKey(songName))
                    songNameToLevels[songName] = new List<BeatmapLevel>();
                songNameToLevels[songName].Add(level);
                levelIdToLevel[level.levelID] = level;
            }

            // Process each folder in the non-legacy directory
            var ostDirs = Directory.GetDirectories(ostBasePath);
            var foldersToDelete = new List<string>();
            foreach (var ostDir in ostDirs)
            {
                try
                {
                    var currentFolderName = Path.GetFileName(ostDir);

                    // Check if already in new format by trying to find a matching level
                    bool alreadyMigrated = false;
                    foreach (var knownLevel in levelIdToLevel.Values)
                    {
                        var expectedFolderName = $"{TheaterFileHelpers.ReplaceIllegalFilesystemChars(knownLevel.songName.Trim())} - {knownLevel.levelID}";
                        if (currentFolderName == expectedFolderName)
                        {
                            alreadyMigrated = true;
                            break;
                        }
                    }

                    if (alreadyMigrated)
                    {
                        _loggingService.Debug($"Already in new format, skipping: {currentFolderName}");
                        continue;
                    }

                    // Try to find matching levels by song name
                    if (!songNameToLevels.TryGetValue(currentFolderName, out var levels))
                    {
                        _loggingService.Debug($"Could not find beatmap level for folder: {currentFolderName}");
                        continue;
                    }

                    // For each matching level, copy the folder to the new name
                    bool copied = false;
                    foreach (var level in levels)
                    {
                        var newFolderName = $"{TheaterFileHelpers.ReplaceIllegalFilesystemChars(level.songName.Trim())} - {level.levelID}";
                        var newPath = Path.Combine(ostBasePath, newFolderName);

                        // If new path already exists, skip
                        if (Directory.Exists(newPath))
                        {
                            _loggingService.Debug($"New path already exists, skipping: {newFolderName}");
                            continue;
                        }

                        // Copy the directory
                        DirectoryCopy(ostDir, newPath, true);
                        _loggingService.Info($"Copied OST video folder from {currentFolderName} to {newFolderName}");
                        copied = true;
                    }

                    // If any copies were made, mark for deletion
                    if (copied)
                    {
                        foldersToDelete.Add(ostDir);
                    }
                }
                catch (Exception e)
                {
                    _loggingService.Warn($"Failed to migrate OST folder {ostDir}:");
                    _loggingService.Warn(e);
                }
            }

            // Clean up old folders
            foreach (var folder in foldersToDelete)
            {
                try
                {
                    Directory.Delete(folder, true);
                    _loggingService.Info($"Deleted old folder: {Path.GetFileName(folder)}");
                }
                catch (Exception e)
                {
                    _loggingService.Warn($"Failed to delete old folder {folder}:");
                    _loggingService.Warn(e);
                }
            }

            _loggingService.Info("Migration of OST videos completed successfully");
        }
        catch (Exception e)
        {
            _loggingService.Error("Error during OST video migration:");
            _loggingService.Error(e);
        }
    }

    private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
    {
        // Get the subdirectories for the specified directory.
        DirectoryInfo dir = new DirectoryInfo(sourceDirName);

        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException(
                "Source directory does not exist or could not be found: "
                + sourceDirName);
        }

        DirectoryInfo[] dirs = dir.GetDirectories();
        // If the destination directory doesn't exist, create it.
        if (!Directory.Exists(destDirName))
        {
            Directory.CreateDirectory(destDirName);
        }

        // Get the files in the directory and copy them to the new location.
        FileInfo[] files = dir.GetFiles();
        foreach (FileInfo file in files)
        {
            string temppath = Path.Combine(destDirName, file.Name);
            file.CopyTo(temppath, false);
        }

        // If copying subdirectories, copy them and their contents to new location.
        if (copySubDirs)
        {
            foreach (DirectoryInfo subdir in dirs)
            {
                string temppath = Path.Combine(destDirName, subdir.Name);
                DirectoryCopy(subdir.FullName, temppath, copySubDirs);
            }
        }
    }

    private async Task MigrateOSTVideosSequentialAsync()
    {
        await MigrateOSTVideosAsync();
        await RenameSongFolders();
    }

    private void OnSongsLoaded(Loader _, ConcurrentDictionary<string, BeatmapLevel> __)
    {
        Task.Run(async () =>
        {
            try
            {
                // Verify BeatmapLevelsModel is available before proceeding
                var beatmapLevels = BeatmapLevelsModel;
                if (beatmapLevels == null)
                {
                    _loggingService.Warn("BeatmapLevelsModel not available yet, skipping migration");
                    return;
                }
            }
            catch (Exception e)
            {
                _loggingService.Warn("Failed to access BeatmapLevelsModel during migration startup:");
                _loggingService.Warn(e);
                return;
            }

            await MigrateOSTVideosSequentialAsync();
        });
    }

    public void Initialize()
    {
        var configs = LoadBundledConfigs().Result;
        foreach (var config in configs) BundledConfigs.TryAdd(config.levelID, config.config);

        // Subscribe to songs loaded event to run migration when levels are available
        Loader.SongsLoadedEvent += OnSongsLoaded;
    }

    public void Dispose()
    {
        Loader.SongsLoadedEvent -= OnSongsLoaded;
    }
}

[Serializable]
internal class BundledConfig
{
    public string levelID = null!;
    public VideoConfig config = null!;
}