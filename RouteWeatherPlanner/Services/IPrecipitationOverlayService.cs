namespace RouteWeatherPlanner.Services;

/// <summary>One radar frame: a moment in time and its tile URL template ({z}/{x}/{y}).</summary>
public sealed record RadarFrame(DateTimeOffset Time, string TileUrlTemplate);

/// <summary>
/// Radar/precipitation map tiles for "what does it look like right now" — a rolling
/// ~2 hour nowcast, NOT valid for a stop hours down the road. See IPointForecastService
/// for arrival-time forecasts and architecture doc §1 for why these are split.
/// </summary>
public interface IPrecipitationOverlayService
{
    /// <summary>
    /// Returns the available radar frames in chronological order (recent-past through
    /// any short-term nowcast the provider offers), each with its own tile URL
    /// template, so the map can animate a loop rather than show one static frame.
    /// </summary>
    Task<IReadOnlyList<RadarFrame>> GetRadarFramesAsync(CancellationToken cancellationToken = default);
}
