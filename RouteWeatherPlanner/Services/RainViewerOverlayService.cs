using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// RainViewer implementation — simple tile-URL API, no key required for the public
/// weather-maps endpoint, generous free tier. See architecture doc §5.
///
/// Returns the full set of recent-past + nowcast frames (not just the latest) so the
/// map can animate a radar loop — same set RainViewer's own reference viewer animates
/// through. See https://github.com/rainviewer/rainviewer-api-example.
///
/// IMPORTANT — RainViewer's radar tiles only exist natively up to zoom level 7;
/// requesting z8+ tiles directly returns a placeholder image saying the zoom level
/// isn't supported (confirmed the hard way — that's what showed up as broken-looking
/// tiles on the map after zooming into a planned route). The fix is on the map side,
/// not here: map.html's radar layers are created with `maxNativeZoom: 7, maxZoom: 19`,
/// so Leaflet stops requesting z8+ from RainViewer and instead upscales the z7 tiles
/// client-side, matching RainViewer's own example.
///
/// Docs: https://www.rainviewer.com/api/weather-maps-api.html
/// </summary>
public sealed class RainViewerOverlayService(HttpClient httpClient) : IPrecipitationOverlayService
{
    private const string WeatherMapsUrl = "https://api.rainviewer.com/public/weather-maps.json";

    public async Task<IReadOnlyList<RadarFrame>> GetRadarFramesAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<WeatherMapsResponse>(WeatherMapsUrl, cancellationToken)
            ?? throw new InvalidOperationException("Radar provider returned an empty response.");

        var frames = new List<RadarFrame>();

        foreach (var f in response.Radar?.Past ?? [])
        {
            frames.Add(BuildFrame(response.Host, f));
        }

        // Nowcast = RainViewer's own short-term extrapolation, roughly the next
        // 30-60 minutes in ~10 minute steps. Tacking it onto the end of the past
        // frames gives a loop that runs from ~2h ago through "about now" — still
        // squarely inside the "what does it look like right now" nowcast window this
        // service is scoped to (architecture doc §1), not a substitute for the
        // arrival-time forecast IPointForecastService provides.
        foreach (var f in response.Radar?.Nowcast ?? [])
        {
            frames.Add(BuildFrame(response.Host, f));
        }

        if (frames.Count == 0)
        {
            throw new InvalidOperationException("No radar frames available.");
        }

        return frames;
    }

    private static RadarFrame BuildFrame(string host, Frame frame)
    {
        // Tile scheme: {host}{path}/{size}/{z}/{x}/{y}/{color}/{options}.png
        // color=2 (universal blue), options=1_1 (smooth, snow shown separately) — tune to taste.
        // Size stays 256 regardless of device pixel ratio to keep this simple; RainViewer's
        // own example swaps in 512 for high-DPI screens if you want sharper tiles later.
        var url = $"{host}{frame.Path}/256/{{z}}/{{x}}/{{y}}/2/1_1.png";
        return new RadarFrame(DateTimeOffset.FromUnixTimeSeconds(frame.Time), url);
    }

    // --- wire types for the RainViewer response subset we use ---

    private sealed class WeatherMapsResponse
    {
        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("radar")]
        public RadarFrames? Radar { get; set; }
    }

    private sealed class RadarFrames
    {
        [JsonPropertyName("past")]
        public List<Frame>? Past { get; set; }

        [JsonPropertyName("nowcast")]
        public List<Frame>? Nowcast { get; set; }
    }

    private sealed class Frame
    {
        [JsonPropertyName("time")]
        public long Time { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }
}
