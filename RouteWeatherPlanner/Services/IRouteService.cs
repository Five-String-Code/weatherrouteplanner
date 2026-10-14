using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Provider-agnostic routing contract. Swap the backing implementation (Azure Maps,
/// Google Routes, Mapbox — see architecture doc §5) without touching ViewModels.
/// </summary>
public interface IRouteService
{
    Task<RouteResult> GetRouteAsync(
        GeoPoint origin,
        GeoPoint destination,
        IReadOnlyList<GeoPoint>? viaPoints,
        CancellationToken cancellationToken = default);
}
