namespace RouteWeatherPlanner.Models;

/// <summary>Provider-agnostic shape returned by IRouteService — see architecture doc §5.</summary>
public sealed class RouteResult
{
    public required IReadOnlyList<GeoPoint> Polyline { get; init; }

    public required IReadOnlyList<RouteLeg> Legs { get; init; }

    public TimeSpan TotalDuration => Legs.Count == 0 ? TimeSpan.Zero : Legs[^1].CumulativeDuration;

    public double TotalDistanceMeters => Legs.Count == 0 ? 0 : Legs[^1].CumulativeDistanceMeters;
}

/// <summary>
/// One leg of the route (e.g. between two turns or waypoints). Cumulative values make
/// it cheap to compute "ETA at any point along the route" without re-summing every time.
/// </summary>
public sealed class RouteLeg
{
    public required GeoPoint Start { get; init; }
    public required GeoPoint End { get; init; }
    public required TimeSpan CumulativeDuration { get; init; }
    public required double CumulativeDistanceMeters { get; init; }
}
