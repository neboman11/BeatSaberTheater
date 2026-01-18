using System;
using BeatSaberPlaylistsLib.Types;
using BeatSaberTheater.Settings;
using BeatSaberTheater.Video.Config;
using Newtonsoft.Json;

namespace BeatSaberTheater.Util;

public static class PlaylistSongUtils
{
    /// <summary>
    /// Do not call this method without checking if BeatSaberPlaylistsLib is installed with <see cref="InstalledMods"/>
    /// </summary>
    public static bool IsPlaylistLevel(this BeatmapLevel? beatmapLevel)
    {
        return beatmapLevel is PlaylistLevel;
    }

    /// <summary>
    /// Do not call this method without checking if BeatSaberPlaylistsLib is installed with <see cref="InstalledMods"/>
    /// </summary>
    public static BeatmapLevel? GetLevelFromPlaylistIfAvailable(this BeatmapLevel? beatmapLevel)
    {
        return beatmapLevel is PlaylistLevel playlistLevel
            ? playlistLevel.playlistSong.BeatmapLevel ?? beatmapLevel
            : beatmapLevel;
    }

    /// <summary>
    /// Do not call this method without checking if BeatSaberPlaylistsLib is installed with <see cref="InstalledMods"/>
    /// </summary>
    public static bool TryGetPlaylistLevelConfig(this BeatmapLevel? beatmapLevel, string levelPath, VideoFormats.Format format,
        out VideoConfig? videoConfig)
    {
        return (videoConfig =
            beatmapLevel is PlaylistLevel playlistLevel ? playlistLevel.TryLoadConfig(levelPath, format) : null) != null;
    }

    /// <summary>
    /// Do not call this method without checking if BeatSaberPlaylistsLib is installed with <see cref="InstalledMods"/>
    /// </summary>
    private static VideoConfig? TryLoadConfig(this PlaylistLevel playlistLevel, string levelPath, VideoFormats.Format format)
    {
        var playlistSong = playlistLevel.playlistSong;
        if (playlistSong.TryGetCustomData("theater", out var theaterData) ||
            playlistSong.TryGetCustomData("cinema", out theaterData))
        {
            VideoConfig? videoConfig;
            try
            {
                var json = JsonConvert.SerializeObject(theaterData);
                videoConfig = JsonConvert.DeserializeObject<VideoConfig>(json);
            }
            catch (Exception e)
            {
                Plugin._log.Error($"Error parsing video json {playlistSong.Name}:");
                Plugin._log.Error(e);
                return null;
            }

            if (videoConfig == null)
            {
                Plugin._log.Warn($"Deserializing video config for {playlistSong.Name} failed");
                return null;
            }

            videoConfig.LevelDir = levelPath;
            videoConfig.UpdateDownloadState(format);

            return videoConfig;
        }

        Plugin._log.Error($"No config exists for {playlistSong.Name}:");
        return null;
    }
}