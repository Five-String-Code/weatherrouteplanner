using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Point-in-time forecast for a single location — the "forecast for Springfield when
/// you arrive at 3:40pm" half of the weather picture (architecture doc §1, §5).
///
/// Deliberately separate from IPrecipitationOverlayService: this answers "what will it
/// be like then," the overlay answers "what does it look like now." Don't merge them —
/// see the "along route" API limitation called out in the architecture doc §5.
/// </summary>
public interface IPointForecastService
{
    Task<CachedForecast> GetForecastAsync(
        GeoPoint location,
        DateTimeOffset arrivalTime,
        CancellationToken cancellationToken = default);
}
