using CoreLocation;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;

namespace RouteWeatherPlanner.Platforms.iOS;

// iOS counterpart of Platforms/Android/TrackingService. iOS suspends an app within
// seconds of it backgrounding, so GpsService's Task.Delay poll loop simply stops
// mid-drive. What keeps an app running instead is an active CLLocationManager session
// that was started while the app was visible, with AllowsBackgroundLocationUpdates on
// and the "location" UIBackgroundModes entry in Info.plist. That works with the same
// While-In-Use permission the app already asks for (no "Always" prompt), the same
// trade Android makes with its location-type foreground service. iOS shows the blue
// location indicator in the status bar while this runs, the equivalent of Android's
// ongoing notification.
//
// Fixes come from the manager's own callbacks rather than MAUI's Geolocation API:
// Geolocation.GetLocationAsync creates a fresh CLLocationManager per request, and a
// manager created while backgrounded doesn't get background updates.
public static class TrackingService
{
    private static CLLocationManager? _manager;

    public static event EventHandler<Location>? LocationUpdated;
    public static event EventHandler<string>? LocationError;

    public static void Start() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_manager is not null) return;

        _manager = new CLLocationManager
        {
            DesiredAccuracy = CLLocation.AccuracyBest,
            // Roughly matches the old 5s poll at highway speed without waking the
            // app for every metre of a traffic jam.
            DistanceFilter = 25,
            ActivityType = CLActivityType.AutomotiveNavigation,
            // Left on, iOS pauses updates when it thinks the device stopped moving
            // (a long red light, a gas stop) and never resumes them in the background.
            PausesLocationUpdatesAutomatically = false,
            AllowsBackgroundLocationUpdates = true,
            ShowsBackgroundLocationIndicator = true,
        };
        _manager.LocationsUpdated += OnLocationsUpdated;
        _manager.Failed += OnFailed;
        _manager.StartUpdatingLocation();
    });

    public static void Stop() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_manager is null) return;

        _manager.StopUpdatingLocation();
        _manager.LocationsUpdated -= OnLocationsUpdated;
        _manager.Failed -= OnFailed;
        _manager.Dispose();
        _manager = null;
    });

    private static void OnLocationsUpdated(object? sender, CLLocationsUpdatedEventArgs e)
    {
        var fix = e.Locations.LastOrDefault();
        if (fix is null) return;

        var location = new Location(fix.Coordinate.Latitude, fix.Coordinate.Longitude,
            new DateTimeOffset((DateTime)fix.Timestamp))
        {
            Accuracy = fix.HorizontalAccuracy,
            Altitude = fix.VerticalAccuracy >= 0 ? fix.Altitude : null,
            Speed = fix.Speed >= 0 ? fix.Speed : null,
            Course = fix.Course >= 0 ? fix.Course : null,
        };
        LocationUpdated?.Invoke(null, location);
    }

    private static void OnFailed(object? sender, Foundation.NSErrorEventArgs e)
    {
        // LocationUnknown is transient (no fix yet, e.g. in a tunnel); the manager
        // keeps trying on its own, so it isn't worth surfacing.
        if (e.Error.Code == (long)CLError.LocationUnknown) return;

        LocationError?.Invoke(null, e.Error.Code == (long)CLError.Denied
            ? "Location permission was revoked."
            : $"GPS error: {e.Error.LocalizedDescription}");
    }
}
