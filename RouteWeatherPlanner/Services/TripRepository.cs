using System.Text.Json;
using RouteWeatherPlanner.Models;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Local persistence for planned trips and their last-known forecasts — what makes the
/// app tolerable when a user loses signal mid-drive (architecture doc §7). Same
/// single-JSON-file approach as FenceRepository: this app's data is small (a handful of
/// trips, each a route + waypoint list), so there's no real case for a database here.
/// </summary>
public class TripRepository : ITripRepository
{
    private readonly string _path =
        Path.Combine(FileSystem.AppDataDirectory, "trips.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public async Task SaveTripAsync(Trip trip, CancellationToken cancellationToken = default)
    {
        var trips = await LoadAllAsync();
        trips.RemoveAll(t => t.Id == trip.Id);
        trips.Add(trip);
        await SaveAllAsync(trips);
    }

    public async Task<Trip?> GetTripAsync(Guid tripId, CancellationToken cancellationToken = default)
    {
        var trips = await LoadAllAsync();
        return trips.FirstOrDefault(t => t.Id == tripId);
    }

    public async Task<List<Trip>> GetRecentTripsAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        var trips = await LoadAllAsync();
        return trips
            .OrderByDescending(t => t.DepartureTime)
            .Take(limit)
            .ToList();
    }

    private async Task<List<Trip>> LoadAllAsync()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var json = await File.ReadAllTextAsync(_path);
            return JsonSerializer.Deserialize<List<Trip>>(json, Opts) ?? new();
        }
        catch { return new(); }
    }

    private async Task SaveAllAsync(List<Trip> trips)
    {
        try
        {
            var json = JsonSerializer.Serialize(trips, Opts);
            await File.WriteAllTextAsync(_path, json);
        }
        catch { /* non-fatal — in-memory trip still works for the rest of this session */ }
    }
}
