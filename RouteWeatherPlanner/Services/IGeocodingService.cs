using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

public record GeocodeResult(double Latitude, double Longitude, string DisplayName);

public interface IGeocodingService
{
    /// <summary>
    /// Resolves a free-text address/place/zip query to coordinates. Call this only on
    /// an explicit user action (Search button, Enter key) — not on every keystroke.
    /// The active implementation (NominatimGeocodingService) is a free public service
    /// whose usage policy forbids live autocomplete; this signature doesn't change if
    /// you swap in AzureMapsGeocodingService later, which doesn't have that
    /// restriction.
    /// </summary>
    Task<IReadOnlyList<GeocodeResult>> SearchAsync(string query, CancellationToken token = default);

    /// <summary>
    /// Resolves a coordinate to a nearby place name (town/city), used to label the
    /// interval-based waypoints WaypointScheduler generates — see
    /// WaypointScheduler.BuildWaypointsAsync. Returns null if no nearby named place
    /// could be resolved (e.g. a remote point with no OSM coverage); callers should
    /// fall back to something else for the label rather than treat this as an error.
    /// </summary>
    Task<string?> ReverseGeocodeAsync(GeoPoint point, CancellationToken token = default);
}
