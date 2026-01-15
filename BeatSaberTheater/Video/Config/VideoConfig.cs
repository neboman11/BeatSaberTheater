using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeatSaberTheater.Download;
using BeatSaberTheater.Models;
using BeatSaberTheater.Settings;
using BeatSaberTheater.Util;
using Newtonsoft.Json;
using SongCore.Data;

namespace BeatSaberTheater.Video.Config;

[Serializable]
[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
public class VideoConfig
{
    public bool? allowCustomPlatform;
    public string? author;
    public float? bloom;
    public bool? bundledConfig;
    public bool? colorBlending;
    public bool? configByMapper;
    public bool? curveYAxis;
    public bool? disableDefaultModifications;
    public int duration; //s
    public float? endVideoAt;
    public string? environmentName;
    public bool? forceEnvironmentModifications;
    public bool? loop;
    public bool? mergePropGroups;
    public int offset; //ms
    public float? playbackSpeed; //percent
    public float? screenHeight;
    public float? screenCurvature;
    public SerializableVector3? screenPosition;
    public SerializableVector3? screenRotation;
    public int? screenSubsurfaces;
    public string? title;
    public bool? transparency;
    public UserSettings? userSettings;
    public string? videoFile;
    public string? videoID;
    public string? videoUrl;

    public ScreenConfig[]? additionalScreens;
    public ColorCorrection? colorCorrection;
    public EnvironmentModification[]? environment;
    public Vignette? vignette;

    [JsonIgnore][NonSerialized] public float DownloadProgress;
    [JsonIgnore][NonSerialized] public float? ConvertingProgress;
    [JsonIgnore][NonSerialized] public DownloadState DownloadState;
    [JsonIgnore][NonSerialized] public string? ErrorMessage;
    [JsonIgnore][NonSerialized] public string? LevelDir;
    [JsonIgnore][NonSerialized] public bool NeedsToSave;
    [JsonIgnore][NonSerialized] public bool PlaybackDisabledByMissingSuggestion;

    [JsonProperty("downloadedFormats")] public Dictionary<VideoFormats.Format, string> DownloadedFormats { get; set; } = new();

    [JsonIgnore] public string? ConfigPath => LevelDir != null ? VideoLoader.GetConfigPath(LevelDir) : null;

    [JsonIgnore]
    public bool EnvironmentModified => (environment != null && environment.Length > 0) || screenPosition != null ||
                                       screenHeight != null;

    [JsonIgnore]
    public bool IsDownloading => DownloadState == DownloadState.Preparing ||
                                 DownloadState == DownloadState.Downloading ||
                                 DownloadState == DownloadState.DownloadingVideo ||
                                 DownloadState == DownloadState.DownloadingAudio;

    [JsonIgnore] public bool IsOfficialConfig => configByMapper is true;

    [JsonIgnore]
    public bool IsPlayable => DownloadState == DownloadState.Downloaded &&
                              !PlaybackDisabledByMissingSuggestion;

    [JsonIgnore]
    public bool IsWIPLevel =>
        LevelDir != null &&
        (LevelDir.Contains(VideoLoader.WIP_MAPS_FOLDER) ||
         SongCore.Loader.SeparateSongFolders.Any(folder =>
         {
             var isWIP =
                 (folder.SongFolderEntry.Pack == FolderLevelPack.CustomWIPLevels || folder.SongFolderEntry.WIP) &&
                 LevelDir.Contains(new DirectoryInfo(folder.SongFolderEntry.Path).Name);
             return isWIP;
         })
        );

    [JsonIgnore] public float PlaybackSpeed => playbackSpeed ?? 1;

    [JsonIgnore]
    public bool TransparencyEnabled => transparency == null ||
                                       (transparency != null && !transparency.Value);

    [JsonIgnore]
    public string? VideoPath
    {
        get
        {
            if (LevelDir != null)
            {
                var path = Directory.GetParent(LevelDir)!.FullName;
                var mapFolderName = new DirectoryInfo(LevelDir).Name;
                // TODO
                // var folder = Path.Combine(path, VideoLoader.WIP_DIRECTORY_NAME, mapFolderName);
                var folder = Path.Combine(path, mapFolderName);
                videoFile = GetVideoFileName(folder);
                path = Path.Combine(folder, videoFile);
                return path;
            }

            if (LevelDir != null)
                try
                {
                    videoFile = GetVideoFileName(LevelDir);
                    return Path.Combine(LevelDir, videoFile);
                }
                catch (Exception e)
                {
                    Plugin._log.Error($"Failed to combine video path for {videoFile}: {e.Message}");
                    return null;
                }

            Plugin._log.Debug("VideoPath is null");
            return null;
        }
    }

    public VideoConfig()
    {
        //Intentionally empty. Used as ctor for JSON deserializer
    }

    public VideoConfig(YTResult searchResult, string levelPath)
    {
        videoID = searchResult.ID;
        title = searchResult.Title;
        author = searchResult.Author;
        duration = searchResult.Duration;

        LevelDir = levelPath;
        videoFile = GetVideoFileName(levelPath);
    }

    public float GetOffsetInSec()
    {
        return offset / 1000f;
    }

    public DownloadState UpdateDownloadState(VideoFormats.Format currentFormat)
    {
        foreach (VideoFormats.Format format in Enum.GetValues(typeof(VideoFormats.Format)))
        {
            string extension = "." + format.ToString().ToLower();
            var path = GetVideoPathWithExtension(extension);
            if (path != null && (videoID != null || videoUrl != null) && File.Exists(path))
            {
                DownloadedFormats[format] = path;
            }
            else
            {
                DownloadedFormats.Remove(format);
            }
        }

        return DownloadState = DownloadedFormats.ContainsKey(currentFormat) ? DownloadState.Downloaded : DownloadState.NotDownloaded;
    }

    private string GetVideoFileName(string levelPath)
    {
        var fileName = videoFile ?? TheaterFileHelpers.ReplaceIllegalFilesystemChars(title ?? videoID ?? "video");
        fileName = TheaterFileHelpers.ShortenFilename(levelPath, fileName);

        if (!Path.HasExtension(fileName))
        {
            fileName += ".mp4";
        }

        return fileName;
    }

    public string? GetVideoPathForFormat(VideoFormats.Format format)
    {
        if (DownloadedFormats.TryGetValue(format, out var path))
        {
            return path;
        }

        // Fallback: check if VideoPath has the correct extension
        string extension = "." + format.ToString().ToLower();
        var fallbackPath = VideoPath;
        if (fallbackPath != null && Path.GetExtension(fallbackPath).ToLower() == extension.ToLower())
        {
            return fallbackPath;
        }

        return null;
    }

    private string? GetVideoPathWithExtension(string extension)
    {
        if (LevelDir != null)
        {
            var path = Directory.GetParent(LevelDir)!.FullName;
            var mapFolderName = new DirectoryInfo(LevelDir).Name;
            var folder = Path.Combine(path, mapFolderName);
            var fileName = videoFile ?? TheaterFileHelpers.ReplaceIllegalFilesystemChars(title ?? videoID ?? "video");
            fileName = TheaterFileHelpers.ShortenFilename(folder, fileName);

            if (!Path.HasExtension(fileName))
            {
                fileName += extension;
            }
            else
            {
                fileName = Path.ChangeExtension(fileName, extension);
            }

            path = Path.Combine(folder, fileName);
            return path;
        }

        if (LevelDir != null)
        {
            try
            {
                var fileName = videoFile ?? TheaterFileHelpers.ReplaceIllegalFilesystemChars(title ?? videoID ?? "video");
                fileName = TheaterFileHelpers.ShortenFilename(LevelDir, fileName);

                if (!Path.HasExtension(fileName))
                {
                    fileName += extension;
                }
                else
                {
                    fileName = Path.ChangeExtension(fileName, extension);
                }

                return Path.Combine(LevelDir, fileName);
            }
            catch (Exception e)
            {
                Plugin._log.Error($"Failed to combine video path for {videoFile}: {e.Message}");
                return null;
            }
        }

        return null;
    }
}