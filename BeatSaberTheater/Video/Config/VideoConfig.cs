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
    public string? VideoPath => GetVideoPathForFormat(VideoFormats.Format.Mp4);

    public VideoConfig()
    {
        //Intentionally empty. Used as ctor for JSON deserializer
        // Ensure DownloadedFormats is initialized as a new instance
        DownloadedFormats = new Dictionary<VideoFormats.Format, string>();
    }

    public VideoConfig(YTResult searchResult, string levelPath)
    {
        videoID = searchResult.ID;
        title = searchResult.Title;
        author = searchResult.Author;
        duration = searchResult.Duration;

        LevelDir = levelPath;

        // Ensure DownloadedFormats is initialized as a new instance
        DownloadedFormats = new Dictionary<VideoFormats.Format, string>();
    }

    public float GetOffsetInSec()
    {
        return offset / 1000f;
    }

    public DownloadState UpdateDownloadState(VideoFormats.Format currentFormat)
    {
        // Remove any invalid entries from DownloadedFormats
        var invalidFormats = DownloadedFormats
            .Where(kvp => !File.Exists(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var format in invalidFormats)
        {
            DownloadedFormats.Remove(format);
        }

        // Try to discover video files that exist but aren't in DownloadedFormats yet
        if (LevelDir != null && (videoID != null || videoUrl != null || videoFile != null))
        {
            foreach (VideoFormats.Format format in Enum.GetValues(typeof(VideoFormats.Format)))
            {
                // Skip if we already have a valid entry for this format
                if (DownloadedFormats.ContainsKey(format) && File.Exists(DownloadedFormats[format]))
                    continue;

                // Try to find the video file with the expected naming
                var videoPath = BuildVideoPath(format);
                if (videoPath != null && File.Exists(videoPath))
                {
                    DownloadedFormats[format] = videoPath;
                }
            }
        }

        // Check if the current format is available
        var hasCurrentFormat = DownloadedFormats.ContainsKey(currentFormat) && File.Exists(DownloadedFormats[currentFormat]);

        return DownloadState = hasCurrentFormat ? DownloadState.Downloaded : DownloadState.NotDownloaded;
    }

    public string? GetVideoPathForFormat(VideoFormats.Format format)
    {
        // First, check if we have this format in DownloadedFormats
        if (DownloadedFormats.TryGetValue(format, out var path) && File.Exists(path))
        {
            return path;
        }

        // Fallback to legacy videoFile property if it matches the format
        if (videoFile != null && LevelDir != null)
        {
            var legacyPath = BuildVideoPath(format);
            if (legacyPath != null && File.Exists(legacyPath))
            {
                // Add it to DownloadedFormats for future use
                DownloadedFormats[format] = legacyPath;
                return legacyPath;
            }
        }

        return null;
    }

    private string? BuildVideoPath(VideoFormats.Format format)
    {
        if (LevelDir == null) return null;

        // Determine the base filename
        // Generate filename from title or videoID
        string baseFileName = TheaterFileHelpers.ReplaceIllegalFilesystemChars(title ?? videoID ?? "video");

        // Get the folder path
        var path = Directory.GetParent(LevelDir)!.FullName;
        var mapFolderName = new DirectoryInfo(LevelDir).Name;
        var folder = Path.Combine(path, mapFolderName);

        // Shorten filename if needed
        baseFileName = TheaterFileHelpers.ShortenFilename(folder, baseFileName);

        // Add the appropriate extension for the format
        var extension = "." + format.ToString().ToLower();
        var fileName = baseFileName + extension;

        return Path.Combine(folder, fileName);
    }
}