using BeatSaberMarkupLanguage;
using HMUI;

namespace BeatSaberTheater.Settings;

public class TheaterSettingsFlowCoordinator : FlowCoordinator
{
    private readonly TheaterSettingsViewController
        _viewController = BeatSaberUI.CreateViewController<TheaterSettingsViewController>();

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        if (firstActivation)
        {
            SetTitle("Theater Settings");
            showBackButton = true;
        }

        if (addedToHierarchy) ProvideInitialViewControllers(_viewController);
    }

    protected override void BackButtonWasPressed(ViewController viewController)
    {
        BeatSaberUI.MainFlowCoordinator.DismissFlowCoordinator(this);
    }
}