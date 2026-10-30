using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Plugin.AdMob;
using Plugin.AdMob.Configuration;
using Polly;
using Polly.Extensions.Http;
using RouteWeatherPlanner.Configuration;
using RouteWeatherPlanner.Services;
using RouteWeatherPlanner.ViewModels;
using RouteWeatherPlanner.Views;

namespace RouteWeatherPlanner;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            // Plugin.AdMob replaces the old Anjo.Android.AppLovin scaffold (see README
            // item 14) — its own MauiAppBuilder extension registers everything the
            // BannerAd XAML control needs, so there's no custom ConfigureMauiHandlers
            // call to write ourselves this time (that was only needed because AppLovin
            // had no MAUI-native control, just a raw platform view to wrap).
            .UseAdMob();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // Falls back to Google's public test ad unit IDs until a real one is set in
        // Configuration/AdMobConfig.cs — see that file's comment. Once you replace the
        // placeholder there, this automatically switches to live ads with no other
        // code change needed.
        AdConfig.UseTestAdUnitIds = AdMobConfig.BannerAdUnitId == AdMobConfig.PlaceholderBannerAdUnitId;

        RegisterServices(builder.Services);
        RegisterViewModelsAndViews(builder.Services);

        return builder.Build();
    }

    private static void RegisterServices(IServiceCollection services)
    {
        // Swap providers here without touching ViewModels — see architecture doc §5.
        // Kept the Polly retry policies even though proximityalarm doesn't use them —
        // a road-trip app hitting flaky signal benefits more from this than a
        // stationary-alarm app does, so it's a deliberate addition, not an oversight.
        //
        // Routing and point-forecast weather are both free/keyless now (OSRM's public
        // demo server, Open-Meteo), same as geocoding — no Azure Maps subscription
        // needed anywhere in the app. AzureMapsRouteService / PointForecastService
        // (Azure) are kept unregistered as reference implementations; swap them back
        // in here if a subscription gets added later for a production-grade routing
        // SLA or an "along route" hazard-flagged forecast.
        services.AddHttpClient<IRouteService, OsrmRouteService>()
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient<IPointForecastService, OpenMeteoForecastService>()
            .AddPolicyHandler(GetRetryPolicy());

        services.AddHttpClient<IPrecipitationOverlayService, RainViewerOverlayService>()
            .AddPolicyHandler(GetRetryPolicy());

        // Address search for the origin/destination fields, same free/keyless OSM
        // Nominatim service proximityalarm uses. Nominatim's usage policy forbids
        // client-side autocomplete, so this is only called on an explicit user
        // action (Search button / Enter key) — see SearchOriginCommand /
        // SearchDestinationCommand on RoutePlannerViewModel, not on every keystroke.
        // AzureMapsGeocodingService is kept in the project as a working reference;
        // swap it back in here if an Azure Maps subscription gets added later and
        // live search-as-you-type is wanted again.
        services.AddHttpClient<IGeocodingService, NominatimGeocodingService>()
            .AddPolicyHandler(GetRetryPolicy());

        services.AddSingleton<IWaypointScheduler, WaypointScheduler>();
        services.AddSingleton<ITripRepository, TripRepository>();

        // Singleton, same as proximityalarm's GpsService registration — one GPS poll
        // loop for the life of the app, not one per page. RoutePlannerViewModel (also
        // a singleton, see below) starts/stops it as a trip is planned.
        services.AddSingleton<IGpsService, GpsService>();

        // No ad-service registration needed anymore — Plugin.AdMob's BannerAd XAML
        // control (see Views/RoutePlanningPage.xaml) handles its own lifecycle once
        // builder.UseAdMob() has run, unlike the old IAdService/AppLovinAdService
        // scaffold this replaced.
    }

    private static void RegisterViewModelsAndViews(IServiceCollection services)
    {
        // Same split as proximityalarm: the root page/VM is a singleton (one map,
        // one in-progress plan for the life of the app); pushed pages are transient
        // so each navigation gets a fresh instance.
        services.AddSingleton<RoutePlannerViewModel>();
        services.AddSingleton<RoutePlanningPage>();

        services.AddTransient<WaypointForecastViewModel>();
        services.AddTransient<ForecastListPage>();
    }

    /// <summary>
    /// Retries transient failures (timeouts, 5xx, 429) with exponential backoff.
    /// Both the routing and weather providers will occasionally fail like this
    /// on a trip through weak signal — see architecture doc §3.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
}
