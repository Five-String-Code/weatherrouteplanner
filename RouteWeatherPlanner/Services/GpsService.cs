using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;

namespace RouteWeatherPlanner.Services;

/// <summary>
/// Ported near-verbatim from proximityalarm's GpsService — same permission flow
/// (LocationWhenInUse, then best-effort LocationAlways for background tracking on
/// API 29+, with AppInfo.ShowSettingsUI() as the denied-permission fallback), same
/// "get last known location immediately, then poll" startup, same foreground-service
/// hand-off so Android doesn't kill tracking when the app backgrounds mid-drive.
///
/// Poll cadence (10s accuracy timeout, 5s delay between polls) is also unchanged from
/// proximityalarm. A car covers more ground per second than a pedestrian a proximity
/// alarm is built for, but RoutePlannerViewModel already throttles what it *does* with
/// each fix (map marker moves on every fix — cheap; ETA/forecast recompute is
/// throttled separately to a few minutes — not cheap, hits Open-Meteo), so there's no
/// need to poll GPS itself any faster than this.
/// </summary>
public class GpsService : IGpsService
{
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public event EventHandler<Location>? LocationUpdated;
    public event EventHandler<string>? LocationError;

    public bool IsTracking { get; private set; }
    public Location? LastKnownLocation { get; private set; }

    private const string DisclosureAcceptedKey = "location_disclosure_accepted";

    // Google Play's Prominent Disclosure and Consent requirement: before the system
    // location permission prompt, show an in-app screen that says what location data is
    // collected, what it is used for, and who it goes to, and get an explicit
    // agree/decline. The OS prompt alone does not count. Remembered after the first
    // "Agree" so it is not shown again.
    public static async Task<bool> EnsureDisclosureAcceptedAsync()
    {
        if (Preferences.Default.Get(DisclosureAcceptedKey, false))
            return true;

        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null)
            return false;

#pragma warning disable CS0618 // DisplayAlert is the long-standing API; newer name is optional
        var accepted = await MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlert(
            "Weatherline uses your location",
            "Weatherline collects your device's precise location (GPS) to show your " +
            "position on the map, update your estimated arrival times, and get weather " +
            "forecasts for places ahead of you on your route.\n\n" +
            "Location is collected only while the app is open or while a trip is being " +
            "tracked (an ongoing notification is shown), and never when the app is " +
            "closed. It is not collected in the background.\n\n" +
            "Nearby coordinates are shared with Open-Meteo to get forecasts. Ads are " +
            "provided by Google AdMob, which may use your device's advertising ID.\n\n" +
            "Tap Agree to continue to the Android location permission, or Decline to " +
            "keep using the app by typing addresses.",
            "Agree",
            "Decline"));
#pragma warning restore CS0618

        if (accepted)
            Preferences.Default.Set(DisclosureAcceptedKey, true);

        return accepted;
    }

    public async Task<bool> RequestPermissionsAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        if (status != PermissionStatus.Granted)
        {
            if (!await EnsureDisclosureAcceptedAsync())
                return false;

            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (status != PermissionStatus.Granted)
        {
            // Android won't re-prompt once previously denied — same fallback as proximityalarm.
            if (status == PermissionStatus.Denied)
            {
                AppInfo.ShowSettingsUI();
            }
            return false;
        }

        // No ACCESS_BACKGROUND_LOCATION request any more (Google Play rejected it).
        // Tracking continues while the app is backgrounded through the location-type
        // foreground service TrackingService starts, which only needs the permission
        // granted above and was started while the app was visible.

        return true;
    }

    public async Task StartTrackingAsync()
    {
        if (IsTracking) return;

        var granted = await RequestPermissionsAsync();
        if (!granted)
        {
            LocationError?.Invoke(this, "Location permission was not granted.");
            return;
        }

        try
        {
            LastKnownLocation = await Geolocation.GetLastKnownLocationAsync();
            if (LastKnownLocation is not null)
                LocationUpdated?.Invoke(this, LastKnownLocation);
        }
        catch (Exception ex)
        {
            LocationError?.Invoke(this, $"Could not get last known location: {ex.Message}");
        }

        _cts = new CancellationTokenSource();
        IsTracking = true;

#if ANDROID
        Platforms.Android.TrackingService.Start();
#endif

        _pollingTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void StopTracking()
    {
        IsTracking = false;
        _cts?.Cancel();
        _cts = null;

#if ANDROID
        Platforms.Android.TrackingService.Stop();
#endif
    }

    public async Task<Location?> GetCurrentLocationAsync()
    {
        var granted = await RequestPermissionsAsync();
        if (!granted)
        {
            LocationError?.Invoke(this, "Location permission was not granted.");
            return null;
        }

        try
        {
            // Medium accuracy, not Best — this is a one-off "where am I roughly
            // starting from" lookup for the origin field, not the fine-grained fix
            // PollLoopAsync wants for tracking a moving vehicle. Medium typically
            // resolves faster, which matters more here since the user is actively
            // waiting on it (the icon button shows a spinner while this runs).
            var request = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10));
            var location = await Geolocation.GetLocationAsync(request);
            if (location is not null) LastKnownLocation = location;
            return location;
        }
        catch (FeatureNotEnabledException)
        {
            LocationError?.Invoke(this, "Device location services are turned off.");
            return null;
        }
        catch (PermissionException)
        {
            LocationError?.Invoke(this, "Location permission was revoked.");
            return null;
        }
        catch (Exception ex)
        {
            LocationError?.Invoke(this, $"Could not get current location: {ex.Message}");
            return null;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10));

        while (!token.IsCancellationRequested)
        {
            try
            {
                var location = await Geolocation.GetLocationAsync(request, token);
                if (location is not null)
                {
                    LastKnownLocation = location;
                    LocationUpdated?.Invoke(this, location);
                }
            }
            catch (FeatureNotEnabledException)
            {
                LocationError?.Invoke(this, "Device location services are turned off.");
            }
            catch (PermissionException)
            {
                LocationError?.Invoke(this, "Location permission was revoked.");
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LocationError?.Invoke(this, $"GPS error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
