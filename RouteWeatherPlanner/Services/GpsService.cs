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

    public async Task<bool> RequestPermissionsAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        if (status != PermissionStatus.Granted)
        {
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

#if ANDROID
        // Background tracking requires the separate ACCESS_BACKGROUND_LOCATION permission on API 29+.
        var bgStatus = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();
        if (bgStatus != PermissionStatus.Granted)
        {
            bgStatus = await Permissions.RequestAsync<Permissions.LocationAlways>();
            if (bgStatus != PermissionStatus.Granted)
            {
                // Foreground-only tracking still works while the app is visible; alert
                // the caller so the UI can explain the limitation (the live marker and
                // ETA recompute will stop updating if the user backgrounds the app).
                LocationError?.Invoke(this, "Background location not granted; live tracking will only update while the app is open.");
            }
        }
#endif

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
