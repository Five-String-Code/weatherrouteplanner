namespace RouteWeatherPlanner.Models;

public enum PrecipitationType
{
    None,
    Rain,
    Snow,
    Ice,
    Mixed
}

/// <summary>
/// A forecast fetched for a specific waypoint/time. Kept distinct from RouteWaypoint so
/// the UI can show "forecast valid for 3:15pm" next to "your ETA is 3:40pm" when a
/// provider's forecast granularity doesn't line up exactly with the computed ETA.
/// </summary>
public sealed class CachedForecast
{
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>The forecast timestamp actually returned by the provider — see summary above.</summary>
    public required DateTimeOffset ForecastFor { get; init; }

    public string Condition { get; init; } = string.Empty;

    public PrecipitationType PrecipType { get; init; } = PrecipitationType.None;

    public double PrecipProbability { get; init; }

    public double TemperatureCelsius { get; init; }

    public double WindSpeedKph { get; init; }

    public IReadOnlyList<string> HazardFlags { get; init; } = [];
}
