using BeatSaberTheater.Services;
using BeatSaberTheater.Util;
using Zenject;

namespace BeatSaberTheater.Installers;

// An installer is where related bindings are grouped together. A binding sets up an object for injection.
// Zenject will handle object creation and figure out what needs to be injected automatically.
// It's recommended to check the Zenject documentation to learn more about dependency injection and why it exists.
// https://github.com/Mathijs-Bakker/Extenject?tab=readme-ov-file#what-is-dependency-injection

// This particular installer relates to bindings that are used during Beat Saber's initialization, and are made
// available in any context, whether that be in the menu, or during a map.
// It is related to the PCAppInit installer in the base game.

internal class AppInstaller : Installer
{
    private readonly PluginConfig _pluginConfig;

    public AppInstaller(PluginConfig pluginConfig)
    {
        _pluginConfig = pluginConfig;
    }

    public override void InstallBindings()
    {
        // This allows the same instance of PluginConfig to be injected into in any class anywhere in the plugin
        Container.BindInstance(_pluginConfig).AsSingle();
        Container.BindInterfacesAndSelfTo<LoggingService>().AsSingle();
        Container.Bind<TheaterCoroutineStarter>().FromNewComponentOnNewGameObject().AsSingle();
        Container.BindInterfacesAndSelfTo<YtDlpUpdateService>().AsSingle();
    }
}