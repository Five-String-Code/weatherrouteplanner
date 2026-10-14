using Microsoft.Maui.Devices.Sensors;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Same contract as proximityalarm's IGpsService — that app already solved
/// "poll GPS in a foreground service and hand fixes to a ViewModel," and a moving
/// road trip needs exactly the same plumbing a stationary proximity alarm does.
/// The only thing that differs here is what the ViewModel does with each fix (move
/// a live map marker, recompute ETAs) — see RoutePlannerViewModel.
/// </summary>
public interface IGpsService
{
    /// <summary>Raised whenever a new location fix is obtained.</summary>
    event EventHandler<Location>? LocationUpdated;

    /// <summary>Raised when a fix could not be obtained (timeout, permission revoked, etc.).</summary>
    event EventHandler<string>? LocationError;

    bool IsTracking { get; }

    Location? LastKnownLocation { get; }

    Task<bool> RequestPermissionsAsync();

    /// <summary>Starts the background polling loop. Safe to call multiple times (no-op if already tracking).</summary>
    Task StartTrackingAsync();

    void StopTracking();

    /// <summary>
    /// Gets a single current-location fix without starting continuous tracking —
    /// used for "use my location" on the origin field, which is a one-off lookup,
    /// not a live trip in progress. Deliberately doesn't touch IsTracking or start
    /// the Android foreground service StartTrackingAsync does; this can run whether
    /// or not a trip is already being tracked. Returns null (and raises
    /// LocationError with the reason) if permission is denied, location services
    /// are off, or the fix otherwise fails.
    /// </summary>
    Task<Location?> GetCurrentLocationAsync();
}
