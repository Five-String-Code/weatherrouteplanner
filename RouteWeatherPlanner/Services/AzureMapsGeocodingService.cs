using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteWeatherPlanner.Configuration;
using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

// NOT CURRENTLY REGISTERED IN MauiProgram.cs. Needs an Azure Maps subscription,
// which the project isn't signing up for right now — NominatimGeocodingService is
// the active IGeocodingService implementation instead (explicit-search UX now,
// not live autocomplete, so it stays within Nominatim's usage policy). Kept this
// file as a working reference: swap it back in via MauiProgram.cs if you do get
// an Azure Maps key later and want live search-as-you-type again.
/// <summary>
/// Address search for the origin/destination fields — same Azure Maps account/key
/// already used for routing and weather (Configuration/ApiKeys.cs).
///
/// This replaced an OSM Nominatim implementation (proximityalarm's own approach)
/// after real-device testing hit a 403: Nominatim's usage policy explicitly forbids
/// client-side autocomplete/search-as-you-type against the public instance —
/// https://operations.osmfoundation.org/policies/nominatim/ — and separately blocks
/// placeholder identifiers like the "example.com" contact email in a User-Agent, which
/// this app's copy (and proximityalarm's) both still had. Search-as-you-type needs a
/// provider that's actually licensed for it; this one is.
///
/// Uses the Search v1.0 "Get Search Fuzzy" endpoint. Azure Maps also has a newer,
/// purpose-built Get Geocode Autocomplete API (api-version 2025-01-01+) that's a
/// closer match for this exact use case, but its response is GeoJSON-shaped
/// differently enough (FeatureCollection/properties/geometry) that it deserved
/// verifying against live docs rather than guessing — worth migrating to later:
/// https://learn.microsoft.com/en-us/azure/azure-maps/migrate-search-v1-api
/// </summary>
public class AzureMapsGeocodingService : IGeocodingService
{
    private const string BaseUrl = "https://atlas.microsoft.com/search/fuzzy/json";

    private readonly HttpClient _http;

    public AzureMapsGeocodingService(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<GeocodeResult>> SearchAsync(string query, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<GeocodeResult>();

        var url = $"{BaseUrl}?api-version=1.0&query={Uri.EscapeDataString(query)}" +
                  $"&limit=5&subscription-key={Uri.EscapeDataString(ApiKeys.AzureMapsKey)}";

        using var response = await _http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var raw = await JsonSerializer.DeserializeAsync<FuzzySearchResponse>(stream, JsonOpts, token);

        if (raw?.Results is null) return Array.Empty<GeocodeResult>();

        return raw.Results
            .Where(r => r.Position is not null)
            .Select(r => new GeocodeResult(
                r.Position!.Lat,
                r.Position.Lon,
                r.Address?.FreeformAddress ?? query))
            .ToList();
    }

    /// <summary>
    /// Azure Maps "Search Address Reverse" v1.0 — same vintage/shape as the Fuzzy
    /// Search endpoint above (results/address.freeformAddress), kept consistent with
    /// it rather than the newer Get Reverse Geocoding API. Not verified against a
    /// live call or current docs (this file is unregistered reference code — see the
    /// header comment), so double-check the response shape before relying on it if
    /// you do swap this back in.
    /// </summary>
    public async Task<string?> ReverseGeocodeAsync(GeoPoint point, CancellationToken token = default)
    {
        var query = $"{point.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{point.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        var url = $"https://atlas.microsoft.com/search/address/reverse/json?api-version=1.0&query={Uri.EscapeDataString(query)}" +
                  $"&subscription-key={Uri.EscapeDataString(ApiKeys.AzureMapsKey)}";

        using var response = await _http.GetAsync(url, token);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var raw = await JsonSerializer.DeserializeAsync<ReverseSearchResponse>(stream, JsonOpts, token);

        return raw?.Addresses?.FirstOrDefault()?.Address?.Municipality
            ?? raw?.Addresses?.FirstOrDefault()?.Address?.FreeformAddress;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class ReverseSearchResponse
    {
        [JsonPropertyName("addresses")]
        public List<ReverseAddressEntry>? Addresses { get; set; }
    }

    private sealed class ReverseAddressEntry
    {
        [JsonPropertyName("address")]
        public ReverseAddress? Address { get; set; }
    }

    private sealed class ReverseAddress
    {
        [JsonPropertyName("municipality")]
        public string? Municipality { get; set; }

        [JsonPropertyName("freeformAddress")]
        public string? FreeformAddress { get; set; }
    }

    private sealed class FuzzySearchResponse
    {
        [JsonPropertyName("results")]
        public List<FuzzyResult>? Results { get; set; }
    }

    private sealed class FuzzyResult
    {
        [JsonPropertyName("position")]
        public FuzzyPosition? Position { get; set; }

        [JsonPropertyName("address")]
        public FuzzyAddress? Address { get; set; }
    }

    private sealed class FuzzyPosition
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }
    }

    private sealed class FuzzyAddress
    {
        [JsonPropertyName("freeformAddress")]
        public string? FreeformAddress { get; set; }
    }
}
