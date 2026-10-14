using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Free, keyless OSM geocoding, same as proximityalarm's NominatimGeocodingService.
/// Nominatim's usage policy explicitly forbids client-side autocomplete/search-as-
/// you-type against this public instance, so this is only called on an explicit user
/// action (Search button / Enter key), never on every keystroke. See
/// RoutePlannerViewModel's SearchOriginCommand/SearchDestinationCommand and
/// RoutePlanningPage.xaml's Entry.ReturnCommand wiring.
/// </summary>
public class NominatimGeocodingService : IGeocodingService
{
    // Public OSM Nominatim instance. Usage policy requires a descriptive User-Agent and
    // caps requests at 1/sec — fine now that search only fires on explicit user action.
    // For production volume, point this at a self-hosted Nominatim instance instead.
    private const string BaseUrl = "https://nominatim.openstreetmap.org/search";
    private const string ReverseUrl = "https://nominatim.openstreetmap.org/reverse";

    private readonly HttpClient _http;

    public NominatimGeocodingService(HttpClient http)
    {
        _http = http;
        // Nominatim's usage policy requires an identifiable User-Agent, and OSM ops has
        // been known to block requests carrying obviously-placeholder identifiers like
        // example.com — this was left as a TODO placeholder through the last two 403s,
        // which is a real, documented candidate cause and worth ruling out first.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RouteWeatherPlannerApp/1.0 (contact: jeff@jeffreyhamby.com)");
    }

    public async Task<IReadOnlyList<GeocodeResult>> SearchAsync(string query, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<GeocodeResult>();

        var url = $"{BaseUrl}?q={Uri.EscapeDataString(query)}&format=jsonv2&limit=5&addressdetails=0";

        using var response = await _http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var raw = await JsonSerializer.DeserializeAsync<List<NominatimEntry>>(stream, JsonOpts, token);

        if (raw is null) return Array.Empty<GeocodeResult>();

        return raw
            .Where(e => double.TryParse(e.Lat, out _) && double.TryParse(e.Lon, out _))
            .Select(e => new GeocodeResult(
                double.Parse(e.Lat, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(e.Lon, System.Globalization.CultureInfo.InvariantCulture),
                e.DisplayName ?? query))
            .ToList();
    }

    /// <summary>
    /// Resolves a waypoint's coordinate to the nearest named place — see
    /// WaypointScheduler.BuildWaypointsAsync, which calls this once per generated
    /// waypoint (paced ~1/sec to stay within Nominatim's usage policy).
    ///
    /// zoom=10 asks Nominatim for city-level detail rather than the default building
    /// level (18) — see the zoom table in https://nominatim.org/release-docs/latest/api/Reverse/.
    /// Verified against Nominatim's current official docs (response shape, address
    /// sub-object field names) rather than a live round-trip call: this sandbox
    /// couldn't reach nominatim.openstreetmap.org's JSON responses directly (requests
    /// went through with no error but came back empty — plausibly the same
    /// identifiable-User-Agent policy this class already works around for SearchAsync,
    /// applied differently by whatever fetched it). Worth a look at the actual place
    /// names on first real run.
    /// </summary>
    public async Task<string?> ReverseGeocodeAsync(GeoPoint point, CancellationToken token = default)
    {
        var url = $"{ReverseUrl}?lat={point.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&lon={point.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  "&format=jsonv2&zoom=10&addressdetails=1";

        using var response = await _http.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
        {
            // Not every coordinate has nearby OSM coverage (e.g. open water, remote
            // areas) — that's a normal "no name available" case, not an error worth
            // surfacing to the user. Caller falls back to showing the ETA instead.
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var raw = await JsonSerializer.DeserializeAsync<NominatimReverseEntry>(stream, JsonOpts, token);

        var address = raw?.Address;
        if (address is null) return null;

        var name = address.City ?? address.Town ?? address.Village ?? address.Hamlet ?? address.County;
        if (string.IsNullOrWhiteSpace(name)) return null;

        return string.IsNullOrWhiteSpace(address.State) ? name : $"{name}, {address.State}";
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private class NominatimEntry
    {
        [JsonPropertyName("lat")] public string Lat { get; set; } = "";
        [JsonPropertyName("lon")] public string Lon { get; set; } = "";
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }

    private sealed class NominatimReverseEntry
    {
        [JsonPropertyName("address")]
        public NominatimAddress? Address { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("town")] public string? Town { get; set; }
        [JsonPropertyName("village")] public string? Village { get; set; }
        [JsonPropertyName("hamlet")] public string? Hamlet { get; set; }
        [JsonPropertyName("county")] public string? County { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
    }
}
