using System;
using BeatSaberTheater.Video.Config;

// ReSharper disable EventNeverSubscribedTo.Global

namespace BeatSaberTheater.Util;

public static class Events
{
    /// <summary>
    /// Indicates if Theater will be doing something on the upcoming song (either play a video or modify the scene).
    /// Will be invoked as soon as the scene transition to the gameplay scene is initiated.
    /// </summary>
    public static event Action<bool>? TheaterActivated;

    /// <summary>
    /// Used by CustomPlatforms to detect whether or not a custom platform should be loaded.
    /// Will be invoked as soon as the scene transition to the gameplay scene is initiated.
    /// </summary>
    public static event Action<bool>? AllowCustomPlatform;

    /// <summary>
    /// Informs about the selected level in Solo or Party mode. Is fired a bit earlier than the BSEvents event.
    /// </summary>
    public static event Action<LevelSelectedArgs>? LevelSelected;

    /// <summary>
    /// Broadcasts SongCores DifficultyData every time the LevelDetailView is refreshed
    /// </summary>
    public static event Action<ExtraSongDataArgs>? DifficultySelected;

    internal static void InvokeSceneTransitionEvents(PluginConfig config, VideoConfig? videoConfig)
    {
        if (!config.PluginEnabled || videoConfig == null)
        {
            TheaterActivated?.Invoke(false);
            AllowCustomPlatform?.Invoke(true);
            return;
        }

        var theaterActivated = videoConfig.IsPlayable || videoConfig.forceEnvironmentModifications == true;
        TheaterActivated?.Invoke(theaterActivated);

        bool allowCustomPlatform;
        if (videoConfig.allowCustomPlatform == null)
            //If the mapper didn't explicitly allow or disallow custom platforms, use global setting
            allowCustomPlatform = !theaterActivated || !config.DisableCustomPlatforms;
        else
            //Otherwise use that setting instead of the global one
            allowCustomPlatform = !theaterActivated || videoConfig.allowCustomPlatform == true;

        AllowCustomPlatform?.Invoke(allowCustomPlatform);
    }

    internal static void SetSelectedLevel(BeatmapLevel? level)
    {
        LevelSelected?.InvokeSafe(new LevelSelectedArgs(level), nameof(LevelSelected));
    }

    internal static void SetExtraSongData(SongCore.Data.SongData? songData,
        SongCore.Data.SongData.DifficultyData? selectedDifficultyData)
    {
        DifficultySelected?.InvokeSafe(new ExtraSongDataArgs(songData, selectedDifficultyData),
            nameof(DifficultySelected));
    }
}