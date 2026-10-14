using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace RouteWeatherPlanner.Platforms.Android;

// Ported from proximityalarm's TrackingService — same foreground-service shape
// (Android kills background location polling within minutes without one), just a
// different channel id and notification copy so it reads correctly for a road trip
// instead of a stationary alarm.
[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeLocation)]
public class TrackingService : Service
{
    private const string ChannelId = "route_weather_tracking_channel";
    private const int NotificationId = 2001;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Weatherline active")
            .SetContentText("Tracking your location to update the map and forecasts along your trip.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuMyLocation)
            .SetOngoing(true)
            .Build();

        StartForeground(NotificationId, notification);

        return StartCommandResult.Sticky;
    }

    private void CreateNotificationChannel()
    {
        var channel = new NotificationChannel(
            ChannelId,
            "Trip Tracking",
            NotificationImportance.Low)
        {
            Description = "Keeps GPS active in the background to update your position and forecasts during a trip."
        };

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(channel);
    }

    public static void Start()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(TrackingService));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop()
    {
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(TrackingService)));
    }
}
