using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RouteWeatherPlanner.Configuration;
using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

// NOT CURRENTLY REGISTERED IN MauiProgram.cs. Needs an Azure Maps subscription key,
// which the project isn't signing up for — OsrmRouteService (free, keyless, public
// OSRM demo server) is the active IRouteService implementation instead. Kept this
// file as a working reference: swap it back in via MauiProgram.cs if you get an Azure
// Maps key later and want its traffic-aware ETA accuracy or production SLA instead of
// the OSRM demo server's best-effort one.
/// <summary>
/// Azure Maps Route Directions implementation. Chosen as the starter provider because
/// it shares an account/SDK with the weather services below — see architecture doc §5
/// for the trade-off against Google Routes / Mapbox.
///
/// Docs: https://learn.microsoft.com/en-us/rest/api/maps/route/get-route-directions
/// </summary>
public sealed class AzureMapsRouteService(HttpClient httpClient) : IRouteService
{
    private const string BaseUrl = "https://atlas.microsoft.com/route/directions/json";

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

        var query = string.Join(':', points.Select(p =>
            $"{p.Latitude.ToString(CultureInfo.InvariantCulture)},{p.Longitude.ToString(CultureInfo.InvariantCulture)}"));

        var url = $"{BaseUrl}?api-version=1.0&query={Uri.EscapeDataString(query)}" +
                  $"&subscription-key={Uri.EscapeDataString(ApiKeys.AzureMapsKey)}";

        var response = await httpClient.GetFromJsonAsync<AzureRouteResponse>(url, cancellationToken)
            ?? throw new InvalidOperationException("Route provider returned an empty response.");

        if (response.Routes is not { Count: > 0 })
        {
            throw new InvalidOperationException("No route found between the requested points.");
        }

        return MapToRouteResult(response.Routes[0]);
    }

    private static RouteResult MapToRouteResult(AzureRoute route)
    {
        var polyline = new List<GeoPoint>();
        var legs = new List<RouteLeg>();

        var cumulativeSeconds = 0d;
        var cumulativeMeters = 0d;

        foreach (var leg in route.Legs ?? [])
        {
            var legStart = leg.Points?.FirstOrDefault();
            var legEnd = leg.Points?.LastOrDefault();

            cumulativeSeconds += leg.Summary?.TravelTimeInSeconds ?? 0;
            cumulativeMeters += leg.Summary?.LengthInMeters ?? 0;

            foreach (var point in leg.Points ?? [])
            {
                polyline.Add(new GeoPoint(point.Latitude, point.Longitude));
            }

            if (legStart is not null && legEnd is not null)
            {
                legs.Add(new RouteLeg
                {
                    Start = new GeoPoint(legStart.Latitude, legStart.Longitude),
                    End = new GeoPoint(legEnd.Latitude, legEnd.Longitude),
                    CumulativeDuration = TimeSpan.FromSeconds(cumulativeSeconds),
                    CumulativeDistanceMeters = cumulativeMeters,
                });
            }
        }

        return new RouteResult { Polyline = polyline, Legs = legs };
    }

    // --- wire types for the Azure Maps response subset we use ---

    private sealed class AzureRouteResponse
    {
        [JsonPropertyName("routes")]
        public List<AzureRoute>? Routes { get; set; }
    }

    private sealed class AzureRoute
    {
        [JsonPropertyName("legs")]
        public List<AzureLeg>? Legs { get; set; }
    }

    private sealed class AzureLeg
    {
        [JsonPropertyName("summary")]
        public AzureLegSummary? Summary { get; set; }

        [JsonPropertyName("points")]
        public List<AzurePoint>? Points { get; set; }
    }

    private sealed class AzureLegSummary
    {
        [JsonPropertyName("lengthInMeters")]
        public double LengthInMeters { get; set; }

        [JsonPropertyName("travelTimeInSeconds")]
        public double TravelTimeInSeconds { get; set; }
    }

    private sealed class AzurePoint
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }
    }
}
