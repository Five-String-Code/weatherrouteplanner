using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Free, keyless routing via the public OSRM demo server (router.project-osrm.org) —
/// same "no subscription" approach as NominatimGeocodingService and
/// RainViewerOverlayService. Replaced AzureMapsRouteService (kept unregistered as a
/// reference) once the project decided not to sign up for an Azure Maps subscription.
///
/// IMPORTANT — this is the public OSRM demo server, not a production SLA: OSRM's own
/// docs describe it as intended for light/evaluation use, and it can rate-limit or
/// degrade under heavy traffic. Fine for a hobby app; for real usage, self-host OSRM
/// (it's open source) or point BaseUrl at your own instance — the response shape
/// (OSRM's HTTP v1 API) won't change.
///
/// Docs: http://project-osrm.org/docs/v5.24.0/api/#route-service
/// </summary>
public sealed class OsrmRouteService : IRouteService
{
    private const string BaseUrl = "https://router.project-osrm.org/route/v1/driving";

    private readonly HttpClient _httpClient;

    public OsrmRouteService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // Real 403 (Forbidden) hit on a live route-planning request — .NET's HttpClient
        // sends no User-Agent by default, and the public OSRM demo server apparently
        // started (or always did, just unverified against a real request before now)
        // rejecting requests without one, same bot-blocking policy already worked
        // around in NominatimGeocodingService. Converted from a primary constructor to
        // a regular one specifically to have somewhere to run this setup — same fix,
        // same identifying string, for consistency across both OSM-family services.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RouteWeatherPlannerApp/1.0 (contact: jeff@jeffreyhamby.com)");
    }

    public async Task<RouteResult> GetRouteAsync(
        GeoPoint origin,
        GeoPoint destination,
        IReadOnlyList<GeoPoint>? viaPoints,
        CancellationToken cancellationToken = default)
    {
        var points = new List<GeoPoint> { origin };
        if (viaPoints is { Count: > 0 })
        {
            points.AddRange(viaPoints);
        }
        points.Add(destination);

        // OSRM takes coordinates as lon,lat (GeoJSON order), semicolon-separated —
        // the opposite order from GeoPoint's own Latitude-then-Longitude convention
        // and from Azure Maps' query string. Easy to get backwards; verified against
        // a live request before shipping this.
        var coords = string.Join(';', points.Select(p =>
            $"{p.Longitude.ToString(CultureInfo.InvariantCulture)},{p.Latitude.ToString(CultureInfo.InvariantCulture)}"));

        var url = $"{BaseUrl}/{coords}?overview=full&geometries=geojson";

        var response = await _httpClient.GetFromJsonAsync<OsrmRouteResponse>(url, cancellationToken)
            ?? throw new InvalidOperationException("Route provider returned an empty response.");

        if (response.Code != "Ok" || response.Routes is not { Count: > 0 })
        {
            throw new InvalidOperationException($"No route found between the requested points (OSRM code: {response.Code}).");
        }

        return MapToRouteResult(response.Routes[0], points);
    }

    private static RouteResult MapToRouteResult(OsrmRoute route, List<GeoPoint> inputPoints)
    {
        // GeoJSON coordinates are [lon, lat] — flip back to our GeoPoint(lat, lon).
        var polyline = (route.Geometry?.Coordinates ?? [])
            .Where(c => c.Length >= 2)
            .Select(c => new GeoPoint(c[1], c[0]))
            .ToList();

        var legs = new List<RouteLeg>();
        var cumulativeSeconds = 0d;
        var cumulativeMeters = 0d;

        var osrmLegs = route.Legs ?? [];
        for (var i = 0; i < osrmLegs.Count; i++)
        {
            var leg = osrmLegs[i];
            cumulativeSeconds += leg.Duration;
            cumulativeMeters += leg.Distance;

            // OSRM's leg objects don't carry their own start/end coordinates — legs
            // correspond 1:1 to consecutive pairs in the input point list (origin,
            // via points, destination), so pull Start/End from there instead of
            // parsing OSRM's snapped `waypoints[]`.
            if (i + 1 < inputPoints.Count)
            {
                legs.Add(new RouteLeg
                {
                    Start = inputPoints[i],
                    End = inputPoints[i + 1],
                    CumulativeDuration = TimeSpan.FromSeconds(cumulativeSeconds),
                    CumulativeDistanceMeters = cumulativeMeters,
                });
            }
        }

        return new RouteResult { Polyline = polyline, Legs = legs };
    }

    // --- wire types for the OSRM response subset we use ---

    private sealed class OsrmRouteResponse
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("routes")]
        public List<OsrmRoute>? Routes { get; set; }
    }

    private sealed class OsrmRoute
    {
        [JsonPropertyName("legs")]
        public List<OsrmLeg>? Legs { get; set; }

        [JsonPropertyName("geometry")]
        public OsrmGeometry? Geometry { get; set; }

        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }
    }

    private sealed class OsrmLeg
    {
        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }
    }

    private sealed class OsrmGeometry
    {
        [JsonPropertyName("coordinates")]
        public List<double[]>? Coordinates { get; set; }
    }
}
