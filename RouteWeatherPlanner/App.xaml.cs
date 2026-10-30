using RouteWeatherPlanner.Views;

namespace RouteWeatherPlanner;

public partial class App : Application
{
    private readonly RoutePlanningPage _mainPage;

    public App(RoutePlanningPage mainPage)
    {
        InitializeComponent();
        _mainPage = mainPage;

        // No manual ad-SDK init call needed here anymore — Plugin.AdMob is
        // initialized entirely through builder.UseAdMob() in MauiProgram.cs, which
        // runs before this constructor. The old AppLovin scaffold needed an explicit
        // Initialize() call here; this replacement doesn't.
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new NavigationPage(_mainPage));
}
