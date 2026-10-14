using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Walks a computed route and produces the list of points we want forecasts for, each
/// with an ETA computed from the trip's departure time — architecture doc §4, step 2-3.
/// </summary>
public interface IWaypointScheduler
{
    /// <summary>
    /// Builds the interval-spaced waypoint list and resolves a place name for each one
    /// via reverse geocoding (see WaypointScheduler for the Nominatim pacing/rate-limit
    /// details) — that's why this is async where BuildWaypoints used to be synchronous.
    /// </summary>
    Task<List<RouteWaypoint>> BuildWaypointsAsync(
        RouteResult route,
        DateTimeOffset departureTime,
        double intervalMeters = 80_000,
        CancellationToken cancellationToken = default);

    /// <summary>Call after the user changes DepartureTime on an already-planned trip.</summary>
    void Reschedule(IEnumerable<RouteWaypoint> waypoints, RouteResult route, DateTimeOffset newDepartureTime);
}
