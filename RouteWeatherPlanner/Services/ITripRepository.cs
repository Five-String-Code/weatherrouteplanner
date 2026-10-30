using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Local persistence for planned trips and their last-known forecasts — what makes the
/// app tolerable when a user loses signal mid-drive (architecture doc §7).
/// </summary>
public interface ITripRepository
{
    Task SaveTripAsync(Trip trip, CancellationToken cancellationToken = default);

    Task<Trip?> GetTripAsync(Guid tripId, CancellationToken cancellationToken = default);

    Task<List<Trip>> GetRecentTripsAsync(int limit = 10, CancellationToken cancellationToken = default);
}
