using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace BeatSaberTheater.Harmony.Patches;

[HarmonyPatch]
[UsedImplicitly]
// ReSharper disable once InconsistentNaming
internal static class StandardLevelScenesTransitionSetupDataSOInit
{
    internal delegate void SceneTransitionDelegate(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey,
        ref OverrideEnvironmentSettings overrideEnvironmentSettings);

    public static SceneTransitionDelegate? SceneTransitionCalled;

    private static MethodInfo TargetMethod()
    {
        return AccessTools.FirstMethod(typeof(StandardLevelScenesTransitionSetupDataSO),
            m => m.Name == nameof(StandardLevelScenesTransitionSetupDataSO.Init));
    }

    [UsedImplicitly]
    public static void Prefix(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey,
        ref OverrideEnvironmentSettings overrideEnvironmentSettings)
    {
        SceneTransitionCalled?.Invoke(beatmapLevel, beatmapKey, ref overrideEnvironmentSettings);
    }
}

[HarmonyPatch]
[UsedImplicitly]
// ReSharper disable once InconsistentNaming
internal static class MissionLevelScenesTransitionSetupDataSOInit
{
    private static MethodInfo TargetMethod()
    {
        return AccessTools.FirstMethod(typeof(MissionLevelScenesTransitionSetupDataSO),
            m => m.Name == nameof(MissionLevelScenesTransitionSetupDataSO.Init));
    }

    [UsedImplicitly]
    private static void Prefix(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey)
    {
        try
        {
            var overrideSettings = new OverrideEnvironmentSettings();
            StandardLevelScenesTransitionSetupDataSOInit.Prefix(beatmapLevel, beatmapKey, ref overrideSettings);
        }
        catch (Exception e)
        {
            Plugin._log.Warn(e);
        }
    }
}