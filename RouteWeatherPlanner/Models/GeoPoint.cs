namespace RouteWeatherPlanner.Models;

public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public override string ToString() => $"{Latitude:F5},{Longitude:F5}";
}
