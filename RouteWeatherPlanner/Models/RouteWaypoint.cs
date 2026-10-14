namespace RouteWeatherPlanner.Models;

/// <summary>
/// A point along the route we want a forecast for — a named town or a fixed-interval
/// fallback point where no town exists (architecture doc §4, step 2).
/// </summary>
public sealed class RouteWaypoint
{
    public required GeoPoint Location { get; init; }

    /// <summary>Nearest town/city name from reverse geocoding, filled in after the
    /// point is created — see WaypointScheduler.BuildWaypointsAsync. Null if no
    /// nearby place could be resolved; the UI falls back to showing the ETA instead.</summary>
    public string? PlaceName { get; set; }

    public required double DistanceFromStartMeters { get; init; }

    /// <summary>Computed from Trip.DepartureTime + cumulative leg duration to this point.</summary>
    public required DateTimeOffset EstimatedArrival { get; set; }

    public CachedForecast? Forecast { get; set; }
}
