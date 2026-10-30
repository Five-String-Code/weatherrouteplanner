using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Free, keyless hourly forecast via Open-Meteo — same "no subscription" approach as
/// NominatimGeocodingService/OsrmRouteService. Replaced PointForecastService (Azure
/// Maps, kept unregistered as a reference) once the project decided not to sign up for
/// an Azure Maps subscription.
///
/// Requests use &amp;timeformat=unixtime with the default GMT timezone rather than
/// &amp;timezone=auto, specifically so `hourly.time` comes back as unambiguous UTC epoch
/// seconds. DateTimeOffset comparisons (picking the closest hour to arrivalTime) are
/// then correct regardless of what timezone the trip crosses into — a real
/// correctness bug it's easy to introduce with &amp;timezone=auto's local-time strings
/// on a route that changes timezones mid-drive.
///
/// Free tier: non-commercial use, up to 10,000 calls/day, attribution required (CC BY
/// 4.0) if this ever ships — see https://open-meteo.com/en/licence. Forecast horizon
/// tops out at 16 days; GetForecastAsync throws for an arrival further out than that
/// (same spirit as the "2-hour along-route" limitation the Azure implementation used
/// to flag — see architecture doc §5).
///
/// Docs: https://open-meteo.com/en/docs
/// </summary>
public sealed class OpenMeteoForecastService(HttpClient httpClient) : IPointForecastService
{
    private const string BaseUrl = "https://api.open-meteo.com/v1/forecast";
    private const int MaxForecastDays = 16;

    public async Task<CachedForecast> GetForecastAsync(
        GeoPoint location,
        DateTimeOffset arrivalTime,
        CancellationToken cancellationToken = default)
    {
        var daysOut = (int)Math.Ceiling((arrivalTime - DateTimeOffset.UtcNow).TotalDays) + 1;
        if (daysOut > MaxForecastDays)
        {
            throw new InvalidOperationException(
                $"Arrival is more than {MaxForecastDays} days out — Open-Meteo's forecast horizon tops out there. " +
                "For longer-range trips this needs a different data source (e.g. a seasonal/climate outlook), not a swap of this call.");
        }
        var forecastDays = Math.Clamp(daysOut, 1, MaxForecastDays);

        var url = $"{BaseUrl}?latitude={location.Latitude.ToString(CultureInfo.InvariantCulture)}" +
                  $"&longitude={location.Longitude.ToString(CultureInfo.InvariantCulture)}" +
                  "&hourly=temperature_2m,precipitation_probability,weather_code,wind_speed_10m" +
                  $"&forecast_days={forecastDays}&timeformat=unixtime";

        var response = await httpClient.GetFromJsonAsync<OpenMeteoResponse>(url, cancellationToken)
            ?? throw new InvalidOperationException("Weather provider returned an empty response.");

        var hourly = response.Hourly;
        if (hourly?.Time is not { Count: > 0 })
        {
            throw new InvalidOperationException("No hourly forecast entries returned for the requested location/time.");
        }

        var targetUnix = arrivalTime.ToUnixTimeSeconds();
        var closestIndex = 0;
        var closestDiff = long.MaxValue;
        for (var i = 0; i < hourly.Time.Count; i++)
        {
            var diff = Math.Abs(hourly.Time[i] - targetUnix);
            if (diff < closestDiff)
            {
                closestDiff = diff;
                closestIndex = i;
            }
        }

        var code = hourly.WeatherCode is { Count: > 0 } && closestIndex < hourly.WeatherCode.Count
            ? hourly.WeatherCode[closestIndex]
            : (int?)null;

        return new CachedForecast
        {
            FetchedAt = DateTimeOffset.UtcNow,
            ForecastFor = DateTimeOffset.FromUnixTimeSeconds(hourly.Time[closestIndex]),
            Condition = DescribeWeatherCode(code),
            PrecipType = MapPrecipType(code),
            PrecipProbability = GetValueOrDefault(hourly.PrecipitationProbability, closestIndex) / 100.0,
            TemperatureCelsius = GetValueOrDefault(hourly.Temperature2m, closestIndex, double.NaN),
            WindSpeedKph = GetValueOrDefault(hourly.WindSpeed10m, closestIndex, double.NaN),
        };
    }

    private static double GetValueOrDefault(List<double>? list, int index, double fallback = 0)
        => list is not null && index < list.Count ? list[index] : fallback;

    // WMO weather interpretation codes — see the table in Open-Meteo's docs
    // (https://open-meteo.com/en/docs, "Weather variable documentation").
    private static PrecipitationType MapPrecipType(int? code) => code switch
    {
        51 or 53 or 55 or 61 or 63 or 65 or 80 or 81 or 82 or 95 => PrecipitationType.Rain,
        56 or 57 or 66 or 67 => PrecipitationType.Ice,
        71 or 73 or 75 or 77 or 85 or 86 => PrecipitationType.Snow,
        96 or 99 => PrecipitationType.Mixed, // thunderstorm with hail
        _ => PrecipitationType.None,
    };

    private static string DescribeWeatherCode(int? code) => code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 => "Fog",
        48 => "Depositing rime fog",
        51 => "Light drizzle",
        53 => "Moderate drizzle",
        55 => "Dense drizzle",
        56 => "Light freezing drizzle",
        57 => "Dense freezing drizzle",
        61 => "Slight rain",
        63 => "Moderate rain",
        65 => "Heavy rain",
        66 => "Light freezing rain",
        67 => "Heavy freezing rain",
        71 => "Slight snow fall",
        73 => "Moderate snow fall",
        75 => "Heavy snow fall",
        77 => "Snow grains",
        80 => "Slight rain showers",
        81 => "Moderate rain showers",
        82 => "Violent rain showers",
        85 => "Slight snow showers",
        86 => "Heavy snow showers",
        95 => "Thunderstorm",
        96 => "Thunderstorm with slight hail",
        99 => "Thunderstorm with heavy hail",
        _ => "Unknown",
    };

    // --- wire types for the Open-Meteo response subset we use ---

    private sealed class OpenMeteoResponse
    {
        [JsonPropertyName("hourly")]
        public OpenMeteoHourly? Hourly { get; set; }
    }

    private sealed class OpenMeteoHourly
    {
        // With &timeformat=unixtime these are epoch seconds (UTC), not ISO8601 strings.
        [JsonPropertyName("time")]
        public List<long>? Time { get; set; }

        [JsonPropertyName("temperature_2m")]
        public List<double>? Temperature2m { get; set; }

        [JsonPropertyName("precipitation_probability")]
        public List<double>? PrecipitationProbability { get; set; }

        [JsonPropertyName("weather_code")]
        public List<int>? WeatherCode { get; set; }

        [JsonPropertyName("wind_speed_10m")]
        public List<double>? WindSpeed10m { get; set; }
    }
}
