using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RouteWeatherPlanner.Configuration;
using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

// NOT CURRENTLY REGISTERED IN MauiProgram.cs. Needs an Azure Maps subscription key,
// which the project isn't signing up for — OpenMeteoForecastService (free, keyless)
// is the active IPointForecastService implementation instead. Kept this file as a
// working reference: swap it back in via MauiProgram.cs if you get an Azure Maps key
// later, e.g. for its "along route" hazard-flagged forecast endpoint (2hr/60-waypoint
// limit — see architecture doc §5) or a paid tier with a longer forecast horizon.
/// <summary>
/// Azure Maps "Get Hourly Forecast" implementation, used for waypoint arrival forecasts.
///
/// IMPORTANT — do not use Azure Maps' "Get Weather Along Route" endpoint for this: it
/// only covers routes completing within 2 hours / 60 waypoints (see architecture doc §5).
/// This hourly-forecast-per-point approach is the one that scales to multi-day trips.
///
/// Docs: https://learn.microsoft.com/en-us/rest/api/maps/weather/get-hourly-forecast
/// For arrivals beyond the hourly horizon (~72h depending on plan), switch this
/// implementation to a daily-forecast call — the interface doesn't need to change.
/// </summary>
public sealed class PointForecastService(HttpClient httpClient) : IPointForecastService
{
    private const string HourlyForecastUrl = "https://atlas.microsoft.com/weather/forecast/hourly/json";

    public async Task<CachedForecast> GetForecastAsync(
        GeoPoint location,
        DateTimeOffset arrivalTime,
        CancellationToken cancellationToken = default)
    {
        var hoursOut = Math.Max(1, (int)Math.Ceiling((arrivalTime - DateTimeOffset.Now).TotalHours));
        var duration = Math.Clamp(hoursOut, 1, 240); // Azure Maps hourly forecast supports up to 240h on some tiers — confirm against your plan.

        var query = $"{location.Latitude.ToString(CultureInfo.InvariantCulture)}," +
                    $"{location.Longitude.ToString(CultureInfo.InvariantCulture)}";

        var url = $"{HourlyForecastUrl}?api-version=1.1&query={Uri.EscapeDataString(query)}" +
                  $"&duration={duration}&subscription-key={Uri.EscapeDataString(ApiKeys.AzureMapsKey)}";

        var response = await httpClient.GetFromJsonAsync<HourlyForecastResponse>(url, cancellationToken)
            ?? throw new InvalidOperationException("Weather provider returned an empty response.");

        var closest = response.Forecasts?
            .OrderBy(f => Math.Abs((f.Date - arrivalTime).Ticks))
            .FirstOrDefault();

        if (closest is null)
        {
            throw new InvalidOperationException("No forecast entries returned for the requested location/time.");
        }

        return new CachedForecast
        {
            FetchedAt = DateTimeOffset.Now,
            ForecastFor = closest.Date,
            Condition = closest.IconPhrase ?? string.Empty,
            PrecipType = MapPrecipType(closest),
            PrecipProbability = (closest.PrecipitationProbability ?? 0) / 100.0,
            TemperatureCelsius = closest.Temperature?.Value ?? double.NaN,
            WindSpeedKph = closest.Wind?.Speed?.Value ?? double.NaN,
        };
    }

    private static PrecipitationType MapPrecipType(HourlyForecastEntry entry)
    {
        if (entry.HasPrecipitation is not true)
        {
            return PrecipitationType.None;
        }

        return entry.PrecipitationType?.ToLowerInvariant() switch
        {
            "rain" => PrecipitationType.Rain,
            "snow" => PrecipitationType.Snow,
            "ice" => PrecipitationType.Ice,
            "mixed" => PrecipitationType.Mixed,
            _ => PrecipitationType.Rain, // provider flagged precipitation but didn't classify it
        };
    }

    // --- wire types for the Azure Maps response subset we use ---

    private sealed class HourlyForecastResponse
    {
        [JsonPropertyName("forecasts")]
        public List<HourlyForecastEntry>? Forecasts { get; set; }
    }

    private sealed class HourlyForecastEntry
    {
        [JsonPropertyName("date")]
        public DateTimeOffset Date { get; set; }

        [JsonPropertyName("iconPhrase")]
        public string? IconPhrase { get; set; }

        [JsonPropertyName("hasPrecipitation")]
        public bool? HasPrecipitation { get; set; }

        [JsonPropertyName("precipitationType")]
        public string? PrecipitationType { get; set; }

        [JsonPropertyName("precipitationProbability")]
        public double? PrecipitationProbability { get; set; }

        [JsonPropertyName("temperature")]
        public ForecastValue? Temperature { get; set; }

        [JsonPropertyName("wind")]
        public WindInfo? Wind { get; set; }
    }

    private sealed class WindInfo
    {
        [JsonPropertyName("speed")]
        public ForecastValue? Speed { get; set; }
    }

    private sealed class ForecastValue
    {
        [JsonPropertyName("value")]
        public double Value { get; set; }
    }
}
