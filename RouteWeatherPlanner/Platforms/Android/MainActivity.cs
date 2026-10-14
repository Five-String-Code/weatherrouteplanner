using Android.App;
using Android.Content.PM;

namespace RouteWeatherPlanner;

// The explicit Name below is what makes `adb shell am start -n
// com.fivestringcode.weatherline/.MainActivity` resolve. Without it, .NET for
// Android derives the compiled Java class name from the C# namespace instead of the
// ApplicationId, so that adb component name won't match anything and you get
// "Error type 3" even though install succeeded — this is the exact bug deploy.ps1
// hit. Same explicit-Name pattern as proximityalarm's MainActivity.
//
// Package history (see README.md's publishing notes for the full story):
// com.routeweatherplanner.app (original, personal-account listing, kept the
// 12-tester requirement even after transfer, later deleted) ->
// com.fivestringcode.weatherroute (fresh org-account listing, but came back as
// already in use when registering it) -> com.fivestringcode.weatherline (current,
// matches the app's actual display name).
[Activity(
    Name = "com.fivestringcode.weatherline.MainActivity",
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges =
        ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
