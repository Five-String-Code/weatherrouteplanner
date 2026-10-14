using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using RouteWeatherPlanner.Models;
using RouteWeatherPlanner.Services;

namespace RouteWeatherPlanner.ViewModels;

/// <summary>
/// Backs ForecastListPage: given a trip, fetches (and caches) an arrival-time forecast
/// for each waypoint. Deliberately uses IPointForecastService, not the radar overlay —
/// see architecture doc §1.
/// </summary>
public class WaypointForecastViewModel : BindableObject
{
    private readonly IPointForecastService _forecastService;
    private readonly ITripRepository _tripRepository;

    public ObservableCollection<RouteWaypoint> Waypoints { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); } }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; private set { _errorMessage = value; OnPropertyChanged(); } }

    private Guid _tripId;

    public ICommand LoadCommand { get; }
    public ICommand RefreshForecastsCommand { get; }

    public WaypointForecastViewModel(IPointForecastService forecastService, ITripRepository tripRepository)
    {
        _forecastService = forecastService;
        _tripRepository = tripRepository;

        LoadCommand = new Command<Guid>(async tripId => await LoadAsync(tripId));
        RefreshForecastsCommand = new Command(async () => await RefreshForecastsAsync());
    }

    public async Task LoadAsync(Guid tripId)
    {
        _tripId = tripId;
        var trip = await _tripRepository.GetTripAsync(tripId);
        if (trip is null)
        {
            ErrorMessage = "Trip not found.";
            return;
        }

        Waypoints.Clear();
        foreach (var waypoint in trip.Waypoints)
        {
            Waypoints.Add(waypoint);
        }

        await RefreshForecastsAsync();
    }

    private async Task RefreshForecastsAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // Sequential on purpose for a first pass — easy to parallelize with a
            // bounded SemaphoreSlim once you've confirmed your provider's rate limits.
            foreach (var waypoint in Waypoints)
            {
                try
                {
                    waypoint.Forecast = await _forecastService.GetForecastAsync(
                        waypoint.Location, waypoint.EstimatedArrival);
                }
                catch
                {
                    // Leave the last-cached forecast (if any) in place and keep going —
                    // one bad waypoint shouldn't blank the whole list.
                }
            }

            var trip = await _tripRepository.GetTripAsync(_tripId);
            if (trip is not null)
            {
                trip.Waypoints = Waypoints.ToList();
                await _tripRepository.SaveTripAsync(trip);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
