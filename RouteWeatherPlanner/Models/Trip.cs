namespace RouteWeatherPlanner.Models;

/// <summary>
/// A planned trip: origin, destination, optional stops, and the computed route/waypoints.
/// See architecture doc §6 for the reasoning behind keeping EstimatedArrival and
/// CachedForecast.ForecastFor as separate fields on RouteWaypoint.
/// </summary>
public sealed class Trip
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public GeoPoint Origin { get; set; }

    public GeoPoint Destination { get; set; }

    public List<GeoPoint> ViaPoints { get; set; } = [];

    /// <summary>
    /// When the trip starts. Defaults to "now" at planning time but the user can set a
    /// future departure — every waypoint ETA and forecast lookup is computed relative
    /// to this value. Changing it after the route is planned requires recomputing all
    /// waypoint ETAs (see WaypointScheduler.Reschedule).
    /// </summary>
    public DateTimeOffset DepartureTime { get; set; } = DateTimeOffset.Now;

    public RouteResult? Route { get; set; }

    public List<RouteWaypoint> Waypoints { get; set; } = [];
}
