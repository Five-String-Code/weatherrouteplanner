using RouteWeatherPlanner.ViewModels;

namespace RouteWeatherPlanner.Views;

public partial class ForecastListPage : ContentPage
{
    private readonly WaypointForecastViewModel _vm;

    public ForecastListPage(WaypointForecastViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _vm = viewModel;
    }

    /// <summary>Called by RoutePlanningPage right after resolving this page from DI,
    /// before pushing it — mirrors passing state into a page the way SavedAlarmsPage
    /// reads off the shared MainViewModel, just via an explicit call since this page
    /// has its own (transient) ViewModel instead of a shared singleton one.</summary>
    public Task LoadTripAsync(Guid tripId) => _vm.LoadAsync(tripId);
}
