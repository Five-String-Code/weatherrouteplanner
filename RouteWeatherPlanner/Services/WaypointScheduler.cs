using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Fixed-interval implementation (default ~50 miles / 80km between points) — the point
/// locations themselves are still interval-based, not an actual "list of towns along
/// the route" dataset (architecture doc §4 step 2 fallback path). What's no longer a
/// TODO: each interval point now gets a real place name via reverse geocoding, so
/// ForecastListPage shows "Huntsville, TX" instead of "Unnamed point" — see
/// BuildWaypointsAsync below.
///
/// Waypoint locations are walked along `route.Polyline` (the actual road-following
/// geometry) by cumulative distance, not interpolated in a straight line between leg
/// endpoints — the earlier version used `GeoPoint(leg.Start, leg.End, fraction)`,
/// which is a straight line from origin to destination. That's fine on a dead-straight
/// highway and visibly wrong on anything with a curve or a turn: the dots would sit
/// off the actual drawn route line, sometimes by miles. Timing still assumes uniform
/// speed across the whole route (proportional to distance, using route-level
/// TotalDuration/TotalDistanceMeters) — accurate for the current reality that this app
/// never requests via points, so there's always exactly one leg; if multi-leg routing
/// gets added later, this stops accounting for legs having different average speeds,
/// same simplification as before, just now stated explicitly.
/// </summary>
public sealed class WaypointScheduler(IGeocodingService geocoding) : IWaypointScheduler
{
    // Nominatim's usage policy caps the public instance at ~1 request/second. A road
    // trip can easily produce 10+ interval waypoints, so this paces the reverse-geocode
    // calls explicitly rather than firing them concurrently — the same category of
    // mistake (hammering Nominatim without pacing) that caused two separate 403s
    // earlier on the origin/destination search fields, this time on a code path that's
    // easy to miss because it's not user-triggered.
    private static readonly TimeSpan ReverseGeocodePacing = TimeSpan.FromMilliseconds(1100);

    private const double EarthRadiusMeters = 6_371_000;

    public async Task<List<RouteWaypoint>> BuildWaypointsAsync(
        RouteResult route,
        DateTimeOffset departureTime,
        double intervalMeters = 80_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        var waypoints = new List<RouteWaypoint>();

        if (route.Legs.Count == 0 || route.Polyline.Count < 2)
        {
            return waypoints;
        }

        var totalDistance = route.TotalDistanceMeters;
        var totalDuration = route.TotalDuration;

        if (totalDistance > 0)
        {
            var nextTargetDistance = intervalMeters;
            var cumulativeDistance = 0.0;

            for (var i = 0; i < route.Polyline.Count - 1 && nextTargetDistance < totalDistance; i++)
            {
                var segmentStart = route.Polyline[i];
                var segmentEnd = route.Polyline[i + 1];
                var segmentLength = HaversineMeters(segmentStart, segmentEnd);

                while (nextTargetDistance < totalDistance && nextTargetDistance <= cumulativeDistance + segmentLength)
                {
                    var fractionIntoSegment = segmentLength <= 0
                        ? 0
                        : (nextTargetDistance - cumulativeDistance) / segmentLength;

                    waypoints.Add(new RouteWaypoint
                    {
                        Location = InterpolateLocation(segmentStart, segmentEnd, fractionIntoSegment),
                        DistanceFromStartMeters = nextTargetDistance,
                        EstimatedArrival = departureTime + totalDuration * (nextTargetDistance / totalDistance),
                    });

                    nextTargetDistance += intervalMeters;
                }

                cumulativeDistance += segmentLength;
            }
        }

        // Always include the destination itself, even if it doesn't land on an interval boundary.
        var lastLeg = route.Legs[^1];
        waypoints.Add(new RouteWaypoint
        {
            Location = lastLeg.End,
            DistanceFromStartMeters = totalDistance,
            EstimatedArrival = departureTime + totalDuration,
        });

        for (var i = 0; i < waypoints.Count; i++)
        {
            try
            {
                waypoints[i].PlaceName = await geocoding.ReverseGeocodeAsync(waypoints[i].Location, cancellationToken);
            }
            catch
            {
                // Non-fatal — leave PlaceName null. ForecastListPage's
                // TargetNullValue fallback covers this; one bad lookup shouldn't
                // abort planning the whole trip.
            }

            if (i < waypoints.Count - 1)
            {
                await Task.Delay(ReverseGeocodePacing, cancellationToken);
            }
        }

        return waypoints;
    }

    public void Reschedule(IEnumerable<RouteWaypoint> waypoints, RouteResult route, DateTimeOffset newDepartureTime)
    {
        // Waypoints were built with distance-proportional timing already baked into the
        // gap between EstimatedArrival values, so shifting the whole trip is a flat
        // offset from the original departure time used to build them.
        var originalDepartureTime = newDepartureTime; // caller must track the prior value if it differs
        foreach (var waypoint in waypoints)
        {
            var elapsedSinceOriginalDeparture = waypoint.EstimatedArrival - originalDepartureTime;
            waypoint.EstimatedArrival = newDepartureTime + elapsedSinceOriginalDeparture;
            waypoint.Forecast = null; // stale — caller should re-fetch
        }
    }

    private static GeoPoint InterpolateLocation(GeoPoint start, GeoPoint end, double fraction) => new(
        start.Latitude + (end.Latitude - start.Latitude) * fraction,
        start.Longitude + (end.Longitude - start.Longitude) * fraction);

    /// <summary>
    /// Great-circle distance between two points, used to walk `route.Polyline`
    /// segment by segment. Polyline segments between consecutive OSRM geometry points
    /// are short enough (well under a kilometer, typically) that the difference
    /// between great-circle and straight-line-on-the-ground distance is negligible —
    /// this doesn't need to be more precise than that for placing an interval marker.
    /// </summary>
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
