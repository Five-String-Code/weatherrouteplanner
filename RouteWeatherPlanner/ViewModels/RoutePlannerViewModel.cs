using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;
using RouteWeatherPlanner.Models;
using RouteWeatherPlanner.Services;

namespace RouteWeatherPlanner.ViewModels;

/// <summary>
/// Backs RoutePlanningPage: takes origin/destination/departure, computes the route,
/// derives waypoints with ETAs, and fetches the current radar overlay for the map.
///
/// Per-waypoint forecasts are now fetched here too (not just on
/// WaypointForecastViewModel/ForecastListPage) so the map's waypoint tooltips can show
/// a time + condition right away, without a round trip to the list page — see
/// PlanRouteAsync. ForecastListPage still independently re-fetches on load/pull-to-
/// refresh via WaypointForecastViewModel; the split from architecture doc §1 (radar
/// overlay vs arrival-time forecast being different problems) still holds, this is
/// just the same arrival-time forecast now being read from two places.
///
/// Origin/destination search fires on an explicit user action (SearchOriginCommand /
/// SearchDestinationCommand, wired to a Search button and the keyboard's Enter/Search
/// key in RoutePlanningPage.xaml) rather than on every keystroke. That's a deliberate
/// difference from proximityalarm's live-as-you-type search box: the active geocoding
/// provider here (NominatimGeocodingService) is a free public service whose usage
/// policy forbids autocomplete, discovered the hard way via a live 403 mid-project.
/// </summary>
public class RoutePlannerViewModel : BindableObject
{
    private readonly IRouteService _routeService;
    private readonly IWaypointScheduler _waypointScheduler;
    private readonly IPrecipitationOverlayService _overlayService;
    private readonly IPointForecastService _forecastService;
    private readonly IGeocodingService _geocoding;
    private readonly ITripRepository _tripRepository;
    private readonly IGpsService _gpsService;

    // ── Live tracking / progress recompute throttling ──────────────────────────
    // Every GPS fix moves the live map marker (cheap, local), but recomputing ETAs
    // and re-fetching forecasts hits Open-Meteo over the network — doing that on
    // every 5-second fix would be wasteful and pointless (a car's ETA doesn't
    // meaningfully change second to second). RecomputeInterval throttles how often
    // that heavier work runs; ForecastDriftThreshold additionally skips re-fetching
    // a forecast for a waypoint whose ETA has barely moved since the last fetch.
    private static readonly TimeSpan RecomputeInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ForecastDriftThreshold = TimeSpan.FromMinutes(15);
    private DateTimeOffset _lastRecomputeAt = DateTimeOffset.MinValue;

    // ── Origin search ────────────────────────────────────────────────────────
    private GeoPoint? _originPoint;

    private string _originSearchText = string.Empty;
    public string OriginSearchText
    {
        get => _originSearchText;
        set
        {
            if (_originSearchText == value) return;
            _originSearchText = value;
            OnPropertyChanged();
            _originPoint = null; // typing again invalidates a previously selected result
        }
    }

    private bool _isLocatingOrigin;
    /// <summary>Backs the origin field's "my location" icon/spinner swap — true
    /// while UseMyLocationCommand's GPS fix is in flight.</summary>
    public bool IsLocatingOrigin
    {
        get => _isLocatingOrigin;
        private set
        {
            _isLocatingOrigin = value;
            OnPropertyChanged();
            ((Command)UseMyLocationCommand).ChangeCanExecute();
        }
    }

    public ObservableCollection<GeocodeResult> OriginSearchResults { get; } = new();

    private bool _hasOriginSearchResults;
    public bool HasOriginSearchResults { get => _hasOriginSearchResults; private set { _hasOriginSearchResults = value; OnPropertyChanged(); } }

    private GeocodeResult? _selectedOriginResult;
    public GeocodeResult? SelectedOriginResult
    {
        get => _selectedOriginResult;
        set
        {
            _selectedOriginResult = value;
            OnPropertyChanged();
            if (value is not null) OnOriginResultSelected(value);
        }
    }

    // ── Destination search ───────────────────────────────────────────────────
    private GeoPoint? _destinationPoint;

    private string _destinationSearchText = string.Empty;
    public string DestinationSearchText
    {
        get => _destinationSearchText;
        set
        {
            if (_destinationSearchText == value) return;
            _destinationSearchText = value;
            OnPropertyChanged();
            _destinationPoint = null;
        }
    }

    public ObservableCollection<GeocodeResult> DestinationSearchResults { get; } = new();

    private bool _hasDestinationSearchResults;
    public bool HasDestinationSearchResults { get => _hasDestinationSearchResults; private set { _hasDestinationSearchResults = value; OnPropertyChanged(); } }

    private GeocodeResult? _selectedDestinationResult;
    public GeocodeResult? SelectedDestinationResult
    {
        get => _selectedDestinationResult;
        set
        {
            _selectedDestinationResult = value;
            OnPropertyChanged();
            if (value is not null) OnDestinationResultSelected(value);
        }
    }

    // ── Trip planning ────────────────────────────────────────────────────────
    private DateTimeOffset _departureTime = DateTimeOffset.Now;
    public DateTimeOffset DepartureTime { get => _departureTime; set { _departureTime = value; OnPropertyChanged(); } }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            ((Command)PlanRouteCommand).ChangeCanExecute();
            ((Command)ClearAllCommand).ChangeCanExecute();
            ((Command)UseMyLocationCommand).ChangeCanExecute();
        }
    }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set { _errorMessage = value; OnPropertyChanged(); } }

    private Trip? _currentTrip;
    public Trip? CurrentTrip
    {
        get => _currentTrip;
        private set
        {
            _currentTrip = value;
            OnPropertyChanged();
            HasCurrentTrip = value is not null;
            ((Command)ViewForecastCommand).ChangeCanExecute();
        }
    }

    private bool _hasCurrentTrip;
    /// <summary>Backs the "View Forecast" button's visibility — nothing to navigate to
    /// until a route has actually been planned.</summary>
    public bool HasCurrentTrip { get => _hasCurrentTrip; private set { _hasCurrentTrip = value; OnPropertyChanged(); } }

    /// <summary>Radar frames (recent-past through RainViewer's short nowcast) for the
    /// animated overlay — see RainViewerOverlayService.GetRadarFramesAsync.</summary>
    private IReadOnlyList<RadarFrame>? _radarFrames;
    public IReadOnlyList<RadarFrame>? RadarFrames { get => _radarFrames; private set { _radarFrames = value; OnPropertyChanged(); } }

    /// <summary>
    /// Raised once a trip is planned and saved — RoutePlanningPage draws the route on
    /// the map but does NOT navigate away. Staying on the map after planning is
    /// deliberate: the point is to see the animated radar over the route, not get
    /// bounced straight to a text list. Use ViewForecastCommand/ViewForecastRequested
    /// for the "take me to the list" action instead.
    /// </summary>
    public event EventHandler<Trip>? TripPlanned;

    /// <summary>Raised when the user taps "View Forecast" — RoutePlanningPage handles
    /// the actual navigation to ForecastListPage (see MainPage's menu-navigation
    /// pattern). Kept as an event rather than having the ViewModel call Navigation
    /// directly, same reasoning as TripPlanned.</summary>
    public event EventHandler<Trip>? ViewForecastRequested;

    /// <summary>Raised whenever a fresh set of radar frames is available — same
    /// event-driven ViewModel-to-View pattern as proximityalarm's Map*Requested
    /// events, just for the radar overlay instead of fences.</summary>
    public event EventHandler<IReadOnlyList<RadarFrame>>? MapRadarOverlayReady;

    /// <summary>Raised on every GPS fix once tracking has started — RoutePlanningPage
    /// moves a "you are here" marker on the map. Deliberately does NOT re-fit/re-center
    /// the map (that would fight the user panning/zooming to watch their own progress),
    /// unlike proximityalarm's updateUserLocation which recenters on first fix — this
    /// app already fits the whole route on Plan Route, and re-centering afterwards
    /// would undo that.</summary>
    public event EventHandler<GeoPoint>? LiveLocationUpdated;

    /// <summary>Raised when GPS tracking hits a recoverable problem (permission denied,
    /// location services off, etc.) — surfaced via ErrorMessage rather than a separate
    /// property since RoutePlanningPage already has a binding/UI for that.</summary>
    public event EventHandler<string>? LiveLocationError;

    /// <summary>Raised after a throttled ETA/forecast recompute changes one or more
    /// upcoming waypoints (architecture doc §7: "periodically recompute ETAs against
    /// actual current location... and re-fetch forecasts... as arrival estimates
    /// shift"). Deliberately separate from TripPlanned: TripPlanned's RouteJs also
    /// redraws the route line and re-fits the map bounds, which would fight the live
    /// marker on every recompute tick. This only updates waypoint popup content.</summary>
    public event EventHandler<Trip>? TripProgressUpdated;

    /// <summary>Raised by ClearAllCommand — RoutePlanningPage wipes the route line,
    /// waypoint markers, radar overlay, and live location marker off the map. Kept
    /// as its own event rather than reusing TripPlanned (with a null Trip) since
    /// TripPlanned's signature is non-nullable and every existing subscriber assumes
    /// a real trip to draw.</summary>
    public event EventHandler? MapCleared;

    /// <summary>Raised whenever the origin point is set — picking a search
    /// suggestion, or "my location". RoutePlanningPage hides the keyboard (picking a
    /// value means the user is done typing, whichever way they set it) and centers
    /// the map on the new point.</summary>
    public event EventHandler<GeoPoint>? OriginSelected;

    /// <summary>Raised when the user picks a destination search suggestion.
    /// RoutePlanningPage hides the keyboard the same way OriginSelected does; no map
    /// re-center for destination, that's origin-only.</summary>
    public event EventHandler<GeoPoint>? DestinationSelected;

    public ICommand PlanRouteCommand { get; }
    public ICommand RefreshRadarCommand { get; }
    public ICommand SearchOriginCommand { get; }
    public ICommand SearchDestinationCommand { get; }
    public ICommand ViewForecastCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand UseMyLocationCommand { get; }

    public RoutePlannerViewModel(
        IRouteService routeService,
        IWaypointScheduler waypointScheduler,
        IPrecipitationOverlayService overlayService,
        IPointForecastService forecastService,
        IGeocodingService geocoding,
        ITripRepository tripRepository,
        IGpsService gpsService)
    {
        _routeService = routeService;
        _waypointScheduler = waypointScheduler;
        _overlayService = overlayService;
        _forecastService = forecastService;
        _geocoding = geocoding;
        _tripRepository = tripRepository;
        _gpsService = gpsService;

        PlanRouteCommand = new Command(async () => await PlanRouteAsync(), () => !IsBusy);
        RefreshRadarCommand = new Command(async () => await RefreshRadarAsync());
        SearchOriginCommand = new Command(async () => await SearchOriginAsync());
        SearchDestinationCommand = new Command(async () => await SearchDestinationAsync());
        ViewForecastCommand = new Command(
            () => { if (CurrentTrip is not null) ViewForecastRequested?.Invoke(this, CurrentTrip); },
            () => CurrentTrip is not null);
        ClearAllCommand = new Command(ClearAll, () => !IsBusy);
        UseMyLocationCommand = new Command(async () => await UseMyLocationAsync(), () => !IsBusy && !IsLocatingOrigin);

        // Same event-driven wiring idiom as proximityalarm's MainViewModel
        // (constructor-subscribes to LocationUpdated/LocationError, translates each
        // fix into a View-facing event). GpsService itself is a singleton and this
        // subscription lives for the app's lifetime, so there's no matching
        // unsubscribe — RoutePlannerViewModel is also registered as a singleton (see
        // MauiProgram), so this is a one-time wiring, not a per-page leak.
        _gpsService.LocationUpdated += OnGpsLocationUpdated;
        _gpsService.LocationError += (_, message) =>
            MainThread.BeginInvokeOnMainThread(() => LiveLocationError?.Invoke(this, message));
    }

    private void OnOriginResultSelected(GeocodeResult result)
    {
        _originPoint = new GeoPoint(result.Latitude, result.Longitude);
        _originSearchText = result.DisplayName; // bypass the setter so this doesn't clear _originPoint again
        OnPropertyChanged(nameof(OriginSearchText));
        OriginSearchResults.Clear();
        HasOriginSearchResults = false;

        // GeocodeResult is a positional record (value equality), so searching the
        // same city again later can return a result that .Equals() this one even
        // though it's a different instance. Left pointed at the old value,
        // CollectionView's SelectedItem binding treats the new list's matching item
        // as already selected — it shows highlighted and tapping it is a no-op from
        // the binding's perspective (setting SelectedItem to an equal value isn't a
        // change), so OnOriginResultSelected never fires again. Clearing the
        // selection back to null here — the same "selection as a momentary action,
        // not persistent state" pattern used for CollectionView taps generally —
        // means every future search starts from a clean slate.
        SelectedOriginResult = null;

        OriginSelected?.Invoke(this, _originPoint.Value);
    }

    private void OnDestinationResultSelected(GeocodeResult result)
    {
        _destinationPoint = new GeoPoint(result.Latitude, result.Longitude);
        _destinationSearchText = result.DisplayName;
        OnPropertyChanged(nameof(DestinationSearchText));
        DestinationSearchResults.Clear();
        HasDestinationSearchResults = false;

        // Same reasoning as OnOriginResultSelected above.
        SelectedDestinationResult = null;

        DestinationSelected?.Invoke(this, _destinationPoint.Value);
    }

    private async Task SearchOriginAsync()
    {
        if (string.IsNullOrWhiteSpace(OriginSearchText))
        {
            OriginSearchResults.Clear();
            HasOriginSearchResults = false;
            return;
        }

        try
        {
            var results = await _geocoding.SearchAsync(OriginSearchText);
            OriginSearchResults.Clear();
            foreach (var r in results) OriginSearchResults.Add(r);
            HasOriginSearchResults = results.Count > 0;
            if (results.Count == 0)
            {
                ErrorMessage = $"No match for \"{OriginSearchText}\" — try a fuller address, city, or zip.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Origin search: {ex.Message}";
        }
    }

    private async Task SearchDestinationAsync()
    {
        if (string.IsNullOrWhiteSpace(DestinationSearchText))
        {
            DestinationSearchResults.Clear();
            HasDestinationSearchResults = false;
            return;
        }

        try
        {
            var results = await _geocoding.SearchAsync(DestinationSearchText);
            DestinationSearchResults.Clear();
            foreach (var r in results) DestinationSearchResults.Add(r);
            HasDestinationSearchResults = results.Count > 0;
            if (results.Count == 0)
            {
                ErrorMessage = $"No match for \"{DestinationSearchText}\" — try a fuller address, city, or zip.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Destination search: {ex.Message}";
        }
    }

    private async Task PlanRouteAsync()
    {
        if (_originPoint is null || _destinationPoint is null)
        {
            ErrorMessage = "Search for and pick an origin and destination first.";
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var origin = _originPoint.Value;
            var destination = _destinationPoint.Value;

            var route = await _routeService.GetRouteAsync(origin, destination, viaPoints: null);
            // Reverse-geocodes each waypoint (paced for Nominatim's rate limit — see
            // WaypointScheduler), so this can take a few seconds longer than the route
            // call alone on a trip with several interval points. IsBusy already covers
            // the whole PlanRouteAsync call, so the Plan Route button just stays
            // disabled a bit longer; no separate loading state needed for this part.
            var waypoints = await _waypointScheduler.BuildWaypointsAsync(route, DepartureTime);

            // Arrival-time forecast per waypoint, so the map tooltips can show it right
            // away — same IPointForecastService/Open-Meteo call WaypointForecastViewModel
            // makes on ForecastListPage, just fetched here too instead of only after a
            // trip to the list page and back. Sequential rather than parallel, same
            // "first pass" reasoning as WaypointForecastViewModel: easy to bound with a
            // SemaphoreSlim later if this gets slow on longer trips. Adds noticeably to
            // the Plan Route wait on top of the reverse-geocode pacing above.
            foreach (var waypoint in waypoints)
            {
                try
                {
                    waypoint.Forecast = await _forecastService.GetForecastAsync(waypoint.Location, waypoint.EstimatedArrival);
                }
                catch
                {
                    // Non-fatal — leave Forecast null. Map tooltip and forecast list
                    // both already handle a missing forecast gracefully.
                }
            }

            var trip = new Trip
            {
                Name = $"{OriginSearchText} → {DestinationSearchText}",
                Origin = origin,
                Destination = destination,
                DepartureTime = DepartureTime,
                Route = route,
                Waypoints = waypoints,
            };

            CurrentTrip = trip;
            await _tripRepository.SaveTripAsync(trip);

            RadarFrames = await _overlayService.GetRadarFramesAsync();
            if (RadarFrames is { Count: > 0 }) MapRadarOverlayReady?.Invoke(this, RadarFrames);

            TripPlanned?.Invoke(this, trip);

            // Start (or restart, if a previous trip was already tracking) live
            // location tracking now that there's a route to show progress against.
            // Fire-and-forget: StartTrackingAsync handles its own permission prompt
            // and reports problems via LocationError -> LiveLocationError rather than
            // blocking Plan Route on the user granting a permission dialog.
            _gpsService.StopTracking();
            _lastRecomputeAt = DateTimeOffset.MinValue;
            _ = _gpsService.StartTrackingAsync();
        }
        catch (Exception ex)
        {
            // Surface a friendly message; the underlying HttpClient already retries
            // transient failures via the Polly policy registered in MauiProgram.
            ErrorMessage = $"Couldn't plan that route: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshRadarAsync()
    {
        try
        {
            RadarFrames = await _overlayService.GetRadarFramesAsync();
            if (RadarFrames is { Count: > 0 }) MapRadarOverlayReady?.Invoke(this, RadarFrames);
        }
        catch
        {
            // Non-fatal — keep showing the last-known overlay rather than blanking it.
        }
    }

    /// <summary>
    /// "My location" icon on the origin field — a one-off GPS fix
    /// (IGpsService.GetCurrentLocationAsync, not the continuous tracking
    /// StartTrackingAsync starts once a trip is planned) reverse-geocoded into a
    /// place name, same as picking a search result does. Falls back to raw
    /// "lat,lon" text (GeoPoint.ToString()) if reverse geocoding fails — the
    /// coordinate itself (_originPoint) is already good either way, so a route can
    /// still be planned even if Nominatim's reverse lookup comes back empty.
    /// </summary>
    private async Task UseMyLocationAsync()
    {
        if (IsLocatingOrigin) return;

        ErrorMessage = null;
        IsLocatingOrigin = true;
        try
        {
            var location = await _gpsService.GetCurrentLocationAsync();
            if (location is null)
            {
                // GpsService already raised LocationError -> LiveLocationError with
                // the specific reason (permission denied, location services off,
                // etc.), which RoutePlanningPage routes into ErrorMessage — nothing
                // further to show here.
                return;
            }

            var point = new GeoPoint(location.Latitude, location.Longitude);
            _originPoint = point;

            string? placeName = null;
            try
            {
                placeName = await _geocoding.ReverseGeocodeAsync(point);
            }
            catch
            {
                // Non-fatal — fall back to coordinates below.
            }

            _originSearchText = placeName ?? point.ToString(); // bypass the setter so this doesn't clear _originPoint again
            OnPropertyChanged(nameof(OriginSearchText));

            OriginSearchResults.Clear();
            HasOriginSearchResults = false;
            SelectedOriginResult = null;

            OriginSelected?.Invoke(this, point);
        }
        finally
        {
            IsLocatingOrigin = false;
        }
    }

    /// <summary>
    /// Resets origin/destination search state and wipes any planned trip off the map
    /// and out of memory. Also stops live tracking — there's no trip left to track
    /// progress against once this runs, and leaving GpsService's foreground service
    /// (and its persistent notification) running with nothing to show for it would
    /// be a confusing, pointless battery drain. Does NOT delete the trip from
    /// ITripRepository's JSON file — this only clears what's currently on screen,
    /// same as how planning a new route already leaves prior saved trips alone.
    /// </summary>
    private void ClearAll()
    {
        OriginSearchText = string.Empty;
        SelectedOriginResult = null;
        OriginSearchResults.Clear();
        HasOriginSearchResults = false;

        DestinationSearchText = string.Empty;
        SelectedDestinationResult = null;
        DestinationSearchResults.Clear();
        HasDestinationSearchResults = false;

        ErrorMessage = null;
        CurrentTrip = null;
        RadarFrames = null;
        _lastRecomputeAt = DateTimeOffset.MinValue;

        _gpsService.StopTracking();

        MapCleared?.Invoke(this, EventArgs.Empty);
    }

    // ── Live tracking ────────────────────────────────────────────────────────

    // IGpsService.LocationUpdated fires from GpsService's background polling Task,
    // not the UI thread — everything this touches (events that drive bindings/JS
    // calls, and the recompute below) needs to be marshaled, same reasoning as
    // RoutePlanningPage.xaml.cs's own MainThread.BeginInvokeOnMainThread use for the
    // keyboard-hide fix.
    private void OnGpsLocationUpdated(object? sender, Location location) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var point = new GeoPoint(location.Latitude, location.Longitude);
            LiveLocationUpdated?.Invoke(this, point);
            await RecomputeProgressAsync(point);
        });

    /// <summary>
    /// Architecture doc §7: "periodically recompute ETAs against actual current
    /// location... and re-fetch forecasts for upcoming waypoints as arrival
    /// estimates shift." Throttled to RecomputeInterval since this makes network
    /// calls (Open-Meteo) per waypoint whose ETA has drifted.
    ///
    /// Progress is estimated by finding the nearest point on the route polyline to
    /// the current fix (nearest-vertex approximation — polyline vertices from OSRM
    /// are dense enough, generally sub-km apart per WaypointScheduler's own comment,
    /// that this is close enough without full segment projection math) and reading
    /// off its cumulative distance from the start. That, plus real elapsed time
    /// since departure, gives an actual average pace for the trip so far, which is
    /// then projected forward onto the remaining waypoints — so a trip running ahead
    /// or behind the original OSRM-duration estimate gets its downstream ETAs (and
    /// therefore forecasts) corrected instead of staying pinned to the original plan.
    /// </summary>
    private async Task RecomputeProgressAsync(GeoPoint currentLocation)
    {
        var trip = CurrentTrip;
        if (trip?.Route is null || trip.Route.Polyline.Count < 2) return;

        var now = DateTimeOffset.Now;
        if (now - _lastRecomputeAt < RecomputeInterval) return;
        _lastRecomputeAt = now;

        var distanceTraveled = DistanceAlongRoute(trip.Route.Polyline, currentLocation);
        var elapsed = now - trip.DepartureTime;
        if (distanceTraveled <= 0 || elapsed <= TimeSpan.Zero) return; // not enough signal yet to project a pace

        var averageSpeedMetersPerSecond = distanceTraveled / elapsed.TotalSeconds;
        if (averageSpeedMetersPerSecond <= 0) return;

        var changedAny = false;
        foreach (var waypoint in trip.Waypoints)
        {
            if (waypoint.DistanceFromStartMeters <= distanceTraveled) continue; // already passed

            var remainingMeters = waypoint.DistanceFromStartMeters - distanceTraveled;
            var newEta = now + TimeSpan.FromSeconds(remainingMeters / averageSpeedMetersPerSecond);

            if ((newEta - waypoint.EstimatedArrival).Duration() < ForecastDriftThreshold) continue;

            waypoint.EstimatedArrival = newEta;
            try
            {
                waypoint.Forecast = await _forecastService.GetForecastAsync(waypoint.Location, newEta);
            }
            catch
            {
                // Non-fatal — keep the previous forecast rather than blanking it out.
            }
            changedAny = true;
        }

        if (!changedAny) return;

        try
        {
            await _tripRepository.SaveTripAsync(trip);
        }
        catch
        {
            // Non-fatal — the in-memory trip (and therefore the map) is still correct
            // even if persisting the updated ETAs fails.
        }

        TripProgressUpdated?.Invoke(this, trip);
    }

    private static double DistanceAlongRoute(IReadOnlyList<GeoPoint> polyline, GeoPoint current)
    {
        var nearestIndex = 0;
        var nearestDistance = double.MaxValue;
        for (var i = 0; i < polyline.Count; i++)
        {
            var distance = HaversineMeters(polyline[i], current);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        var cumulative = 0.0;
        for (var i = 0; i < nearestIndex; i++)
        {
            cumulative += HaversineMeters(polyline[i], polyline[i + 1]);
        }

        return cumulative;
    }

    // Same formula as WaypointScheduler.HaversineMeters (duplicated rather than
    // shared — it's a five-line self-contained calculation and pulling it into a
    // shared static utility for one other caller isn't worth the indirection).
    private const double EarthRadiusMeters = 6_371_000;

    private static double HaversineMeters(GeoPoint a, GeoPoint b)
    {
        var lat1 = DegreesToRadians(a.Latitude);
        var lat2 = DegreesToRadians(b.Latitude);
        var deltaLat = DegreesToRadians(b.Latitude - a.Latitude);
        var deltaLon = DegreesToRadians(b.Longitude - a.Longitude);

        var h = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));

        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
