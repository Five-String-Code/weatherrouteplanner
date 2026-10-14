using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using RouteWeatherPlanner.Models;
using RouteWeatherPlanner.Services;
using RouteWeatherPlanner.ViewModels;

namespace RouteWeatherPlanner.Views;

public partial class RoutePlanningPage : ContentPage
{
    private readonly RoutePlannerViewModel _vm;
    private bool _mapReady;

    public RoutePlanningPage(RoutePlannerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _vm = viewModel;

        // No banner wiring needed here anymore — AdUnitId is now bound declaratively
        // in RoutePlanningPage.xaml via x:Static, so Plugin.AdMob's BannerAd control
        // handles its own lifecycle without any code-behind involvement.

        // Planning a trip draws the route and starts the animated radar loop but
        // deliberately does NOT navigate away — the "View Forecast" button (bound to
        // ViewForecastCommand) is the only thing that takes you to ForecastListPage
        // now. Same DI-resolve-and-push pattern as MainPage's menu navigation, just
        // triggered by the user instead of automatically.
        _vm.ViewForecastRequested += async (_, trip) =>
        {
            var forecastPage = IPlatformApplication.Current!.Services.GetRequiredService<ForecastListPage>();
            await forecastPage.LoadTripAsync(trip.Id);
            await MainThread.InvokeOnMainThreadAsync(() => Navigation.PushAsync(forecastPage));
        };

        // Map updates: same event-driven ViewModel -> View pattern as proximityalarm's
        // MainPage (Map*Requested events driving JsAsync calls), just for the route
        // polyline/waypoints and radar overlay instead of fences.
        _vm.TripPlanned += async (_, trip) => await JsAsync(RouteJs(trip));
        _vm.MapRadarOverlayReady += async (_, frames) => await JsAsync(RadarFramesJs(frames));

        // Live GPS tracking (starts automatically once a trip is planned — see
        // RoutePlannerViewModel.PlanRouteAsync): move the "you are here" marker on
        // every fix, and refresh waypoint popup content on the ViewModel's throttled
        // ETA/forecast recompute. LiveLocationError reuses the same ErrorMessage
        // surface as every other failure path on this page rather than adding a
        // second error UI.
        _vm.LiveLocationUpdated += async (_, point) =>
            await JsAsync($"updateUserLocation({point.Latitude.ToJs()},{point.Longitude.ToJs()});");
        _vm.LiveLocationError += (_, message) =>
            MainThread.BeginInvokeOnMainThread(() => _vm.ErrorMessage = message);
        _vm.TripProgressUpdated += async (_, trip) => await JsAsync(WaypointPopupsJs(trip));

        // Clear All: wipes the route/waypoints/radar/live marker off the map — the
        // ViewModel side (RoutePlannerViewModel.ClearAll) already reset the
        // search/trip state and stopped GPS tracking before raising this.
        _vm.MapCleared += async (_, _) => await JsAsync("clearAll();");

        // Origin/Destination often still have focus after tapping Plan Route (or
        // hitting Search then Plan Route without tapping elsewhere first), which
        // keeps the soft keyboard covering the map.
        //
        // Entry.Unfocus() alone does NOT reliably hide the Android soft keyboard in
        // .NET MAUI — this is a confirmed, still-open upstream bug (dotnet/maui#21053,
        // "It is not possible to unfocus an Entry", closed as "not planned" by the
        // MAUI team), not something fixable by changing when Unfocus() is called. The
        // previous fix here (moving this from TripPlanned to IsBusy-going-false, to
        // dodge a suspected overlay/refocus race) didn't actually address that and
        // the keyboard kept not hiding — worth recording so that isn't retried.
        // DismissKeyboard() below calls Android's InputMethodManager directly, which
        // is the documented workaround for this bug.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RoutePlannerViewModel.IsBusy) || _vm.IsBusy) return;
            DismissKeyboard();
        };

        // Picking a search suggestion (or "my location") means the user is done
        // typing in that field, but tapping a CollectionView row below the Entry
        // doesn't take focus away from it on its own — the keyboard used to stay up
        // until Plan Route was tapped and finished (the IsBusy-false path above).
        // Dismissing right on selection, for both fields, closes that gap. Origin
        // additionally centers the map on the newly-picked point.
        _vm.OriginSelected += (_, point) =>
        {
            DismissKeyboard();
            _ = JsAsync($"centerMap({point.Latitude.ToJs()},{point.Longitude.ToJs()});");
        };
        _vm.DestinationSelected += (_, _) => DismissKeyboard();

        MapWebView.Navigated += OnMapNavigated;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_mapReady) await LoadMapAsync();
    }

    private async Task LoadMapAsync()
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("map.html");
            using var reader = new StreamReader(stream);
            MapWebView.Source = new HtmlWebViewSource { Html = await reader.ReadToEndAsync() };
        }
        catch (Exception ex)
        {
            _vm.ErrorMessage = $"Map load error: {ex.Message}";
        }
    }

    private void OnMapNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success) return;
        _mapReady = true;

        // Replay current state in case the trip/radar were already set before the
        // WebView finished its first load (e.g. returning to this page).
        if (_vm.CurrentTrip is not null) _ = JsAsync(RouteJs(_vm.CurrentTrip));
        if (_vm.RadarFrames is { Count: > 0 }) _ = JsAsync(RadarFramesJs(_vm.RadarFrames));
    }

    // Shared by every "the user is done with this field" trigger (Plan Route
    // finishing, picking a search suggestion, "my location"): unfocus both Entries
    // and call the direct Android IME dismiss below. Always marshaled to the main
    // thread since two of those three triggers (OriginSelected/DestinationSelected)
    // fire from ViewModel code that isn't guaranteed to already be on it.
    private void DismissKeyboard() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OriginEntry.Unfocus();
            DestinationEntry.Unfocus();
            HideKeyboard();
        });

    // Direct Android IME dismiss — the actual fix for dotnet/maui#21053. #if ANDROID
    // means this compiles to a no-op on other platforms (this project only targets
    // Android right now, but this keeps it safe if/when net10.0-ios gets added,
    // per the original Google Play → iOS plan in the architecture doc).
    private static void HideKeyboard()
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        var token = activity?.CurrentFocus?.WindowToken;
        if (token is null) return;

        if (activity!.GetSystemService(Android.Content.Context.InputMethodService)
            is Android.Views.InputMethods.InputMethodManager imm)
        {
            imm.HideSoftInputFromWindow(token, Android.Views.InputMethods.HideSoftInputFlags.None);
        }
#endif
    }

    private async Task JsAsync(string script)
    {
        if (!_mapReady) return;
        await MainThread.InvokeOnMainThreadAsync(
            () => MapWebView.EvaluateJavaScriptAsync(script));
    }

    private static string RouteJs(Trip trip)
    {
        var points = trip.Route?.Polyline ?? [];
        var pointsJs = "[" + string.Join(",", points.Select(p =>
            $"[{p.Latitude.ToJs()},{p.Longitude.ToJs()}]")) + "]";

        // map.html's advanceWaypointHighlight() highlights these in route order as
        // the radar animation ticks, so this list needs to stay in Trip.Waypoints'
        // existing order (origin-to-destination) — no ETA needed here anymore, that
        // ETA-matching approach was tried and replaced (see map.html's comment on
        // advanceWaypointHighlight for why).
        var waypointsJs = "[" + string.Join(",", trip.Waypoints.Select(w =>
            $"[{w.Location.Latitude.ToJs()},{w.Location.Longitude.ToJs()},{JsStr(WaypointPopupHtml(w))}]")) + "]";

        return $"setRoute({pointsJs}); setWaypoints({waypointsJs});";
    }

    // Leaflet's bindPopup (called from map.html's setWaypoints) renders its string
    // argument as HTML, not plain text — that's what makes the <b>/<br/> here work.
    // RoutePlannerViewModel.PlanRouteAsync now fetches a forecast per waypoint
    // specifically so this has something to show besides the place/ETA it always had.
    private static string WaypointPopupHtml(RouteWaypoint w)
    {
        var name = HtmlEscape(w.PlaceName ?? "Waypoint");
        // "t" (short time) under InvariantCulture renders as 24-hour "HH:mm" with no
        // AM/PM designator — InvariantCulture's ShortTimePattern just doesn't include
        // one. A custom "h:mm tt" pattern gives 12-hour + AM/PM regardless of culture.
        var eta = w.EstimatedArrival.ToString("h:mm tt", CultureInfo.InvariantCulture);

        if (w.Forecast is not { } forecast)
        {
            return $"<b>{name}</b><br/>ETA {eta}<br/><i>Forecast unavailable</i>";
        }

        var tempLine = double.IsNaN(forecast.TemperatureCelsius)
            ? "temp unavailable"
            : $"{(forecast.TemperatureCelsius * 9.0 / 5.0 + 32).ToString("0", CultureInfo.InvariantCulture)}°F";

        var condition = HtmlEscape(forecast.Condition is { Length: > 0 } c ? c : "Unknown");
        var precipPct = (forecast.PrecipProbability * 100).ToString("0", CultureInfo.InvariantCulture);

        return $"<b>{name}</b><br/>ETA {eta}<br/>{condition}, {tempLine}<br/>{precipPct}% precip";
    }

    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string RadarFramesJs(IReadOnlyList<RadarFrame> frames)
    {
        var itemsJs = string.Join(",", frames.Select(f =>
            $"{{time:{f.Time.ToUnixTimeSeconds()},url:{JsStr(f.TileUrlTemplate)}}}"));
        return $"setRadarFrames([{itemsJs}]);";
    }

    // Waypoints must stay in trip.Waypoints' existing order here too — map.html's
    // updateWaypointPopups matches this array to waypointMarkers by index, same
    // ordering assumption RouteJs's setWaypoints call already relies on.
    private static string WaypointPopupsJs(Trip trip)
    {
        var popupsJs = "[" + string.Join(",", trip.Waypoints.Select(w =>
            JsStr(WaypointPopupHtml(w)))) + "]";
        return $"updateWaypointPopups({popupsJs});";
    }

    private static string JsStr(string s) => $"'{s.Replace("'", "\\'")}'";
}

file static class DoubleExtensions
{
    public static string ToJs(this double d) =>
        d.ToString("G", CultureInfo.InvariantCulture);
}
