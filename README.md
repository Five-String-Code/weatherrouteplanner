# Route Weather Planner — starter scaffold

Source-code skeleton for the app designed in `route-weather-app-architecture.md`.
.NET MAUI, targeting Google Play first, then iOS. Structure and coding patterns
follow `proximityalarm` (and by extension, speed limit): flat `Models/`/`Services/`
/`ViewModels/`/`Views/`/`Converters/` folders, plain `BindableObject`+`Command`
ViewModels, `NavigationPage` navigation, hand-rolled converters, JSON-file
persistence, and — discovered partway through getting this running on a real device —
a `WebView` + Leaflet.js + OpenStreetMap map instead of `Microsoft.Maui.Controls.Maps`,
same as proximityalarm's own map. No CommunityToolkit.Mvvm, no CommunityToolkit.Maui,
no Shell, no SQLite, no Google Maps API key.

## This is not a fully build-ready project by itself

This skeleton was hand-written in a sandbox without the MAUI workload (no Android/iOS
SDKs available there), so it was never compiled here — you're the build verification.
It includes a minimal `Resources/AppIcon/`, `Resources/Splash/` (plain placeholder
SVGs, purple + a pin glyph, just enough for the build to succeed), empty
`Resources/Images/`, `Resources/Fonts/`, `Resources/Raw/`, and now
`Platforms/Android/MainActivity.cs` + `MainApplication.cs` (mirroring
proximityalarm's, including the explicit `Name = "com.routeweatherplanner.app.MainActivity"`
on the `[Activity]` attribute — without that, `adb shell am start -n
com.routeweatherplanner.app/.MainActivity` can't resolve the activity even after a
successful build+install, because .NET for Android otherwise derives the compiled
Java class name from the C# namespace, not the ApplicationId. Learned that one the
hard way against a real device this round).

`Platforms/Android/AndroidManifest.xml` now exists too (see item 8 below — needed once
live location tracking landed, since the permissions it requires aren't part of the
.NET Android tooling's auto-generated default manifest the project relied on before).

Still missing: `Platforms/iOS/` (AppDelegate.cs, Info.plist) and `Properties/` — real
platform bootstrapping/manifest files that need to come from the template. If you
don't already have a `Platforms/` folder from one, generate one on your dev machine:

```bash
dotnet workload install maui
dotnet new maui -n RouteWeatherPlanner
```

...and copy `Platforms/iOS/` and `Properties/` from that over — but keep this
scaffold's `Platforms/Android/MainActivity.cs`, `MainApplication.cs`,
`AndroidManifest.xml`, and `TrackingService.cs` rather than the template's: the
explicit `Name` on `MainActivity` is what makes `deploy.ps1` able to launch the app by
component name, and the manifest/`TrackingService.cs` are what live location tracking
(item 8 below) needs — the template's default manifest won't have the location/
foreground-service/notification permissions or the `<service>` declaration.

Delete `AppShell.xaml(.cs)` and the empty `Data/`, `Routing/`, `Weather/`,
`TripPlanning/` folders if you still see them here — leftovers from earlier layout
passes that a sandbox permissions quirk stopped from being cleaned up. Nothing
references them.

Then:

```bash
cp Configuration/ApiKeys.Sample.cs Configuration/ApiKeys.cs   # already present here — just edit the key
dotnet build -t:Run -f net10.0-android   # or net10.0-ios, with a device/emulator running
```

Or use `deploy.ps1` for the clean/build/adb-install/launch sequence.

## Folder structure

```
RouteWeatherPlanner/
  Models/       Trip, RouteResult/RouteLeg, RouteWaypoint, GeoPoint, CachedForecast/PrecipitationType
  Services/     Interfaces + implementations together (no separate Infrastructure split):
                  IRouteService / OsrmRouteService                    (free, keyless — public OSRM demo server; AzureMapsRouteService kept unused as reference)
                  IPointForecastService / OpenMeteoForecastService   (free, keyless — arrival-time forecast per waypoint; PointForecastService (Azure) kept unused as reference)
                  IPrecipitationOverlayService / RainViewerOverlayService   (radar/precip map tiles)
                  IGeocodingService / NominatimGeocodingService     (address search, explicit-action only — AzureMapsGeocodingService kept unused as a reference, see note below)
                  IWaypointScheduler / WaypointScheduler            (route -> waypoints with ETAs)
                  ITripRepository / TripRepository                  (JSON-file persistence, like FenceRepository)
  ViewModels/   RoutePlannerViewModel, WaypointForecastViewModel — plain BindableObject + Command
  Views/        RoutePlanningPage (root page, hosts the map WebView), ForecastListPage
                (pushed via NavigationPage)
  Converters/   InvertedBoolConverter, StringNotEmptyConverter — declared per-page in
                <ContentPage.Resources>, same as SettingsPage's NotEmptyConverter
  Resources/Raw/map.html   Leaflet + OpenStreetMap map (route polyline, waypoint
                markers, radar overlay) — same WebView-map pattern as proximityalarm's
                own map.html, driven from RoutePlanningPage.xaml.cs via
                EvaluateJavaScriptAsync, same as MainPage does for fences
  Configuration/  API keys (gitignored) — not currently needed by anything registered in
                  MauiProgram.cs. Geocoding, routing, and point-forecast weather are all
                  free/keyless now (Nominatim, OSRM, Open-Meteo), same as proximityalarm's
                  own approach. Kept only because the Azure Maps implementations are still
                  in the project as unused reference code, in case a subscription gets
                  added later.
  App.xaml(.cs), MauiProgram.cs   Composition root — App resolves RoutePlanningPage via
                                  constructor injection and wraps it in a NavigationPage,
                                  same as App/MainPage in proximityalarm.
```

## Deliberate deviations from proximityalarm

I matched folder layout, MVVM style, navigation, converters, and persistence. A few
things stayed different, not as style choices but because this app needs capabilities
(or hit constraints) proximityalarm doesn't:

- **`Configuration/ApiKeys.cs`** — no longer actually needed. The original plan was
  Azure Maps for routing/weather, which does need a key; after that hit a 401 (see
  "Before this actually works" below) the project moved to OSRM + Open-Meteo instead,
  both free and keyless, same as proximityalarm's Nominatim-only approach. This folder
  is now dead weight kept around only because the Azure implementations are still in
  the project as reference code — delete it if you're confident you won't swap back.
- **Polly retry policies in `MauiProgram.cs`/`.csproj`** — proximityalarm makes no
  third-party HTTP calls worth retrying. This app's entire value proposition is API
  calls succeeding mid-drive on flaky signal, so the retry/backoff policy earns its
  keep here in a way it wouldn't over there.
- **Address search: explicit action, not search-as-you-type** — proximityalarm's own
  search box fires a debounced search on every keystroke. That's exactly what OSM
  Nominatim's usage policy forbids for its free public instance ("auto-complete search
  is strictly forbidden"), and it surfaced here as a live 403 while testing on device.
  Tried swapping to Azure Maps Search next, which also 403'd for reasons that weren't
  pinned down before the decision was made to drop the subscription requirement
  entirely for now. The actual fix that matters is the interaction model, not the
  provider: `SearchOriginCommand`/`SearchDestinationCommand` now fire one geocoding
  call only when the user taps Search or presses Enter, never on keystroke. That's
  what keeps Nominatim (free, keyless) policy-compliant. `AzureMapsGeocodingService.cs`
  is kept in the project, unregistered, as a working reference if a subscription gets
  added later. proximityalarm's own search box still has the original live-autocomplete
  policy conflict — worth fixing there too if it starts 403ing.

## Before this actually works

1. ~~API keys~~ Not needed anymore — nothing registered in `MauiProgram.cs` requires a
   subscription. `Configuration/ApiKeys.cs` and its placeholder value can stay
   untouched unless you swap back to one of the Azure Maps implementations.
2. ~~Geocoding~~ Done, differently than originally planned — `SearchOriginCommand` /
   `SearchDestinationCommand` on `RoutePlannerViewModel` fire one `IGeocodingService.
   SearchAsync()` call when the user taps Search or presses Enter (not on every
   keystroke), populate a suggestions dropdown, and resolve to a `GeoPoint` on
   selection. Backed by `NominatimGeocodingService` (free, keyless OSM, same as
   proximityalarm) — no subscription required. This works for full addresses, city
   names, and zip codes since Nominatim's `/search` endpoint handles all three as
   free-text queries; a bare zip with nothing else can occasionally return a wider
   area than expected, so encourage "city, state zip" over a zip alone if precision
   matters. `AzureMapsGeocodingService.cs` is kept, unregistered, as a reference if a
   subscription gets added later for live autocomplete.
3. ~~Routing~~ Done — `OsrmRouteService` calls the public OSRM demo server
   (`router.project-osrm.org`), free and keyless. Verified against a live request
   during development (`/route/v1/driving/{lon},{lat};{lon},{lat}?overview=full&geometries=geojson`),
   so the response parsing is confirmed against real data, not just docs. The one real
   caveat: this is OSRM's public *demo* server, meant for light/evaluation use per
   their own docs — it can rate-limit or degrade under heavy traffic. Fine for
   personal use; self-host OSRM (open source) if this ever needs a production SLA.
   `AzureMapsRouteService.cs` is kept, unregistered, as a reference.
4. ~~Point-forecast weather~~ Done — `OpenMeteoForecastService` calls Open-Meteo
   (`api.open-meteo.com`), free and keyless, non-commercial use up to 10,000 calls/day
   (attribution required under CC BY 4.0 if this ships — see
   https://open-meteo.com/en/licence). Forecast horizon tops out at 16 days;
   `GetForecastAsync` throws a clear error for arrivals further out than that.
   **Unlike the routing swap above, this one is verified against Open-Meteo's
   published API docs, not a live test call** — `api.open-meteo.com` wasn't reachable
   from the sandbox this was built in (network restriction on that specific host,
   confirmed separately from your device's own connectivity), so the JSON field names
   (`hourly.time`/`temperature_2m`/`precipitation_probability`/`weather_code`/
   `wind_speed_10m`, `&timeformat=unixtime` behavior) are transcribed from Open-Meteo's
   current docs rather than round-tripped here. Worth a close look at the actual
   forecast values on first real run. `PointForecastService.cs` (Azure) is kept,
   unregistered, as a reference.
5. ~~Named towns for waypoints~~ Partially done — `WaypointScheduler` still *places*
   a waypoint every ~50 miles (`intervalMeters`), not at an actual town, but each one
   now gets a real name via `IGeocodingService.ReverseGeocodeAsync`
   (`NominatimGeocodingService`, zoom=10/city-level) instead of showing "Unnamed
   point." `BuildWaypoints` became `BuildWaypointsAsync` for this. Two things worth
   knowing: it paces the reverse-geocode calls ~1.1s apart to respect Nominatim's
   1 request/second policy, so planning a route with several waypoints now visibly
   takes a few seconds longer; and the *locations* are still fixed intervals, not
   actual named towns along the route, so you can still land in the middle of nowhere
   and get back the nearest town's name rather than a town the route actually passes
   through. Swapping in a places dataset (picking waypoints near towns in the first
   place, not just naming wherever the interval lands) is the remaining half of this.
5b. ~~Waypoint tooltips are name/ETA only~~ Done — `RoutePlannerViewModel.PlanRouteAsync`
   now also fetches an arrival-time forecast per waypoint (same `IPointForecastService`
   call `WaypointForecastViewModel` makes on `ForecastListPage`, just fetched here too),
   and `RoutePlanningPage.xaml.cs`'s `WaypointPopupHtml` builds a small HTML popup —
   place name, ETA, condition, temp (converted to °F), precip chance — that
   `map.html`'s existing `bindPopup(w[2])` renders as-is (Leaflet popups render HTML,
   not just text). This is a second fetch of the same forecast the list page also
   pulls, not a shared cache — acceptable at this scale, but worth noting if Open-Meteo
   call volume ever matters. Also stacks with item 5's reverse-geocode pacing, so
   Plan Route on a multi-waypoint trip now does: route call, then N paced
   reverse-geocode calls, then N forecast calls — noticeably slower than any one of
   those alone, which is why there's now a busy overlay while it runs (item 9 below).
5c. ~~Waypoint dots didn't sit on the drawn route~~ Fixed — `WaypointScheduler` used to
   place each interval waypoint by interpolating a straight line between
   `route.Legs[i].Start`/`.End`. Since this app never requests via points, that's
   always a single leg spanning the whole trip — i.e. a straight line from origin
   straight to destination, regardless of how much the actual road curves. Visibly
   wrong on anything but a dead-straight highway. `BuildWaypointsAsync` now walks
   `route.Polyline` (the real road-following geometry from OSRM) segment by segment
   using great-circle (Haversine) distance, so each dot sits exactly on the line
   `setRoute()` draws. Timing math didn't change — still proportional to distance
   using the whole route's total duration, same simplifying assumption as before,
   just now applied to the correct locations.
6. ~~Radar tile layer~~ Done, animated — `setRadarFrames()` in `map.html` layers the
   full set of RainViewer past+nowcast frames over the base OpenStreetMap layer and
   cycles through them on a timer at partial opacity, no platform-specific code
   needed. Radar tiles from RainViewer only exist natively up to zoom 7; the layers
   are created with `maxNativeZoom: 7, maxZoom: 19` so Leaflet upscales instead of
   requesting unsupported zoom levels directly (that omission originally showed up as
   tiles reading "zoom level not supported" once you zoomed into a planned route).
   `RoutePlannerViewModel.MapRadarOverlayReady` fires whenever a fresh frame set is
   available; `RoutePlanningPage.xaml.cs` forwards it to the map via JS. Planning a
   route no longer auto-navigates to the forecast list — it stays on the map so you
   can watch the radar loop, and a "View Forecast Along Route" button
   (`ViewForecastCommand` / `ViewForecastRequested`) does the navigation on demand.
6b. ~~Waypoint dots don't relate to the radar animation~~ Done, on the second attempt
   — `map.html`'s `advanceWaypointHighlight()` swaps one waypoint's dot at a time to a
   bigger, pulsing yellow icon, advancing in route order (origin toward destination)
   on the same clock tick that steps the radar frame. This is a rhythm, not a literal
   time match: the first version tried highlighting a waypoint only when a radar
   frame's real timestamp was close to that waypoint's ETA, which looked broken in
   practice — RainViewer's frames only span roughly the past 2 hours through a short
   nowcast (architecture doc §1), so on anything longer than a short local trip only
   the very first waypoint ever fell inside that window and everything past it just
   stayed dark for the whole loop. Confirmed that's what was actually happening, not
   a bug, then switched to this sweep instead. `RouteJs` in
   `RoutePlanningPage.xaml.cs` went back to sending plain `[lat, lon, popupHtml]`
   triples — the ETA-unix-timestamp 4th element the time-matching version needed
   isn't used anymore.
6c. ~~Waypoint sweep and radar loop don't restart together~~ Fixed, in two passes.
   First pass only handled radar-finishing-first: `waypointHighlightIndex` and
   `radarPosition` each cycled modulo a different array length (waypoint count vs.
   radar frame count), which are essentially never equal, so the two loops silently
   drifted apart instead of restarting together — fixed by resetting the sweep back
   to waypoint 0 whenever the radar loop wrapped to its first frame. That only
   covered one direction, though: on a short trip with fewer waypoints than radar
   frames (the common case — a handful of waypoints vs. RainViewer's ~13-frame
   past+nowcast set), the sweep finishes its own loop well before the radar does,
   and it either idled or restarted on its own out of step with the radar. Second
   pass in `map.html`'s `playRadarAnimation` made it symmetric: whichever loop is
   about to complete its own cycle on a given tick forces *both* back to frame
   0/waypoint 0 together, so the two always restart in lockstep regardless of which
   one is shorter. (Guarded against the degenerate 0-or-1-waypoint case, where
   "index mod 1" is always 0 and would otherwise look like the sweep finishing every
   single tick and freeze the radar on its first frame.)
7. **List-item live updates**: `WaypointForecastViewModel` mutates
   `RouteWaypoint.Forecast` in place after fetching. Since `RouteWaypoint` isn't a
   `BindableObject`, the `CollectionView` won't auto-refresh that binding — either
   replace list items instead of mutating them, or make `RouteWaypoint` observable.
8. ~~Location permissions / live tracking~~ Done — ported proximityalarm's
   `IGpsService`/`GpsService` (same permission flow: `LocationWhenInUse`, then a
   best-effort `LocationAlways` request for background tracking on API 29+, falling
   back to `AppInfo.ShowSettingsUI()` if denied) and `TrackingService.cs` (foreground
   service, so Android doesn't kill GPS polling once the app backgrounds mid-drive —
   renamed channel/notification copy for a trip context instead of an alarm).
   `Platforms/Android/AndroidManifest.xml` is a new file — this project had none at
   all before this (see the top of this README): the location, foreground-service,
   and notification permissions it needs aren't part of the .NET Android tooling's
   auto-generated default manifest the way `INTERNET` apparently already was.

   `RoutePlannerViewModel` starts tracking automatically once `PlanRouteAsync`
   succeeds (restarting it if a previous trip was already tracking) and reacts to
   each GPS fix two ways: `LiveLocationUpdated` moves a blue "you are here" marker on
   the map (`map.html`'s `updateUserLocation` — deliberately never re-centers/re-fits
   the map after the first fix, unlike proximityalarm's version, since `setRoute()`
   already fit the whole route and re-centering on the user would undo that); and a
   throttled (every 2 minutes) recompute projects the trip's actual average pace so
   far (real elapsed time vs. distance traveled, found via nearest-vertex
   approximation against `route.Polyline`) onto the remaining waypoints, re-fetching
   a waypoint's Open-Meteo forecast only if its ETA has drifted by more than 15
   minutes — this is the "periodically recompute ETAs... and re-fetch forecasts...
   as arrival estimates shift" behavior architecture doc §7 originally called for but
   left undetailed. `TripProgressUpdated` pushes the refreshed popup HTML into
   `map.html`'s `updateWaypointPopups()`, which only touches popup content — not the
   route line, bounds, or highlight sweep — so it doesn't fight whatever the user is
   currently looking at on the map.

   Not done: there's still no dedicated "trip complete" action — the "Clear All"
   button added later (item 10 below) doubles as a manual stop (it calls
   `IGpsService.StopTracking()`), but there's nothing that stops tracking
   automatically on arrival. Fine for a single-trip use case; worth adding if this
   needs to detect "you've reached the destination" on its own.
9. ~~Plan Route feels unresponsive while it runs~~ Done — two additions on
   `RoutePlanningPage`: a full-page dimmed overlay with an `ActivityIndicator` +
   "Planning route…" label shows whenever `RoutePlannerViewModel.IsBusy` is true
   (which, per item 5b above, can now be a real several-second wait, not just the
   route call); and once `IsBusy` goes back to false, the Origin/Destination `Entry`s
   get unfocused and the Android soft keyboard gets dismissed directly.
   That second part took two tries to get right, worth recording so it isn't retried:
   `Entry.Unfocus()` by itself does **not** reliably hide the Android keyboard in
   .NET MAUI — it's a confirmed, still-open upstream bug
   ([dotnet/maui#21053](https://github.com/dotnet/maui/issues/21053), closed "not
   planned" by the MAUI team), not something fixable by changing *when* `Unfocus()`
   is called (moving it from `TripPlanned` to `IsBusy`-going-false was tried first and
   didn't help, for exactly this reason). `RoutePlanningPage.xaml.cs`'s
   `HideKeyboard()` calls Android's `InputMethodManager.HideSoftInputFromWindow`
   directly via `#if ANDROID`, which is the documented workaround. It's a no-op on
   other platforms — relevant if `net10.0-ios` gets added later per the architecture
   doc's original Google Play → iOS plan, at which point this same "keyboard won't
   dismiss" problem will need an iOS-side equivalent (resigning the first responder).
10. ~~No way to clear a planned trip / start over~~ Done — a "Clear All" button next
    to Plan Route (`RoutePlannerViewModel.ClearAllCommand`) resets both search boxes,
    clears the saved-selection state, drops `CurrentTrip`/`RadarFrames`, and stops
    live GPS tracking (`IGpsService.StopTracking()` — there's no trip left to track
    against, so leaving the foreground service and its notification running would
    just be a pointless battery drain). It fires a new `MapCleared` event that
    `RoutePlanningPage.xaml.cs` turns into a single `clearAll()` JS call;
    `map.html`'s `clearAll()` just composes the existing `setRoute([])`,
    `setWaypoints([])`, `clearRadarFrames()`, and a new `clearUserLocation()` (removes
    the blue "you are here" marker) rather than duplicating any of their logic.
    Doesn't touch `ITripRepository`'s saved JSON — this only clears what's currently
    on screen, same as planning a new route already does to whatever was there
    before.
11. ~~Reselecting an already-picked city showed a highlighted, unselectable
    suggestion~~ Fixed — `GeocodeResult` is a positional `record` (value equality:
    same lat/lon/display name means `.Equals()` true across different instances).
    `SelectedOriginResult`/`SelectedDestinationResult` were never reset after a
    selection was handled, so if you picked a city, planned a route, and searched
    that same city again, the new result list's matching entry `.Equals()`-matched
    the still-set `SelectedOriginResult` — `CollectionView` showed it pre-highlighted
    (the orange selection color), and tapping it did nothing, because setting
    `SelectedItem` to a value that's already equal to the current one isn't treated
    as a change by the binding, so `OnOriginResultSelected` never re-fired.
    `OnOriginResultSelected`/`OnDestinationResultSelected` now reset the selection
    back to `null` right after handling it — the same "selection is a momentary
    action, not persisted state" pattern `ClearAllCommand` (item 10) also relies on
    — so every fresh search starts with nothing selected.
12. ~~No way to use your current location as the origin~~ Done — a 📍 icon sits
    inside the origin field's right edge (layered in the same Grid cell as the
    `Entry`, not a separate column, so it reads as "inside the textbox") bound to
    the new `UseMyLocationCommand`. Swaps to an `ActivityIndicator` while the fix is
    in flight (`IsLocatingOrigin`). Uses a new `IGpsService.GetCurrentLocationAsync()`
    — a single `GeolocationAccuracy.Medium` fix, deliberately separate from
    `StartTrackingAsync()`'s continuous polling loop, since this is a one-off lookup
    for a field, not a live trip: it doesn't touch `IsTracking` or start the Android
    foreground service, and can be called whether or not a trip is already being
    tracked. The result is reverse-geocoded into a place name the same way a
    waypoint's name is (`IGeocodingService.ReverseGeocodeAsync`), falling back to
    raw `"lat,lon"` text if that lookup fails — the coordinate itself is already
    good either way, so Plan Route still works even without a resolved name.
    Permission handling and failure messaging (denied permission, location services
    off, etc.) reuse `GpsService`'s existing `LocationError` event, which
    `RoutePlanningPage.xaml.cs` already routes into `ErrorMessage` for live tracking
    — no new error-surfacing plumbing needed for this.
13. ~~Keyboard stayed up after picking an address, and origin selection didn't move
    the map~~ Fixed — the existing keyboard-hide workaround (item 9, `dotnet/maui#21053`)
    only ran once `PlanRouteAsync` finished (`IsBusy` going back to `false`), so
    picking a search suggestion left the keyboard covering the map until Plan Route
    was tapped separately. `RoutePlannerViewModel` now raises `OriginSelected`/
    `DestinationSelected` right when a suggestion (or, for origin, "my location") is
    picked; `RoutePlanningPage.xaml.cs` pulled the unfocus+IME-dismiss logic into a
    shared `DismissKeyboard()` and calls it from all three triggers instead of just
    the one. `OriginSelected` additionally calls a new `centerMap(lat, lon)` in
    `map.html` (pans/zooms to roughly city-level on the picked point) — there's no
    route to fit yet at that point in the flow, so without this the map just sits on
    its initial world view until Plan Route runs `setRoute()`'s own `fitBounds()`.
14. **Google AdMob banner ad** (via `Plugin.AdMob`) — this replaced an earlier AppLovin
    MAX integration; the AppLovin history and why it got swapped out are kept below for
    the record, then the current AdMob setup and what you need to do next.

    **Why AppLovin got replaced:** the AppLovin scaffold went through five real,
    sequential build-time fights before it finally compiled and dexed clean — a
    `NU1107` version conflict, a `deploy.ps1` PowerShell encoding bug, three binding
    types needing an `I` interface prefix the original guess missed, an ambiguous
    `Application` reference, and a duplicate-class dexing error — all because
    `Anjo.Android.AppLovin` is a raw .NET-for-Android binding of a native SDK with no
    official MAUI package, so nothing about its C# surface could be confirmed without
    a real compile. On top of that, AppLovin's own account approval (needed for an SDK
    key) sat unanswered for several days with no ETA. Given a MAUI-native alternative
    exists with none of those problems, it made more sense to switch than keep fighting
    a binding library account approval was also blocking.

    **What's different about `Plugin.AdMob`:** it's free, open-source (MIT), and built
    specifically for .NET MAUI rather than being a raw native binding — it ships its
    own `<BannerAd>` XAML control and a `builder.UseAdMob()` MAUI app-builder
    extension, so this integration needed no custom `ViewHandler` at all (contrast
    `Platforms/Android/BannerAdViewHandler.cs`, which the AppLovin version needed and
    this one doesn't). It also handles GDPR consent (Google's User Messaging Platform)
    automatically before requesting ads, and — the part that matters most while you're
    between AdMob approval and a live ad unit ID — has first-class support for
    Google's public test ads with zero setup.

    **What it does:** an anchored banner sits at the bottom of `RoutePlanningPage`,
    below the map, never overlapping it (`RoutePlanningPage.xaml`'s outer `Grid` has a
    second `Auto` row for it, unchanged from the AppLovin layout). The banner's
    `AdUnitId` is bound declaratively via `x:Static` to
    `Configuration/AdMobConfig.BannerAdUnitId` — no code-behind wiring needed.
    `MauiProgram.cs` compares that value against `AdMobConfig.PlaceholderBannerAdUnitId`
    at startup and sets `Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = true`
    whenever they match, so **the banner shows real Google test ads out of the box,
    with the placeholder config checked into this delivery** — you should see an ad
    the first time you build and run, before touching AdMob at all. Once you replace
    the placeholder with your real ad unit ID (steps below), that flag flips to
    `false` automatically and the app starts requesting live ads.

    **What you need to do to get real (non-test) ads showing:**
    1. Go to [admob.google.com](https://admob.google.com) and sign in with a Google
       account. Account approval is typically same-day to 24 hours — much faster than
       AppLovin's multi-day silence.
    2. In the AdMob console, **Apps > Add app**. Choose "Yes" when asked if the app is
       published on a supported app store, and search for it by name/package
       (`com.fivestringcode.weatherline`) once your Play Store listing is up — this
       is the step you're publishing the current build for. If you'd rather not wait,
       choosing "No" lets you add it manually and switch it to "published" later, but
       linking the real Play listing now avoids re-doing this step.
    3. Copy the **App ID** AdMob generates (format `ca-app-pub-XXXXXXXXXXXXXXXX~
       XXXXXXXXXX`, note the `~`) and paste it into
       `Platforms/Android/AndroidManifest.xml`, replacing the
       `ca-app-pub-3940256099942544~3347511713` placeholder (that placeholder is
       Google's own published test App ID — safe to leave committed, just not what
       you want for live ads).
    4. In the same app's console, **Ad units > Add ad unit > Banner**. Name it
       whatever you like (e.g. "Route Planner Banner"). Copy the **Ad unit ID**
       (format `ca-app-pub-XXXXXXXXXXXXXXXX/XXXXXXXXXX`, note the `/`, no `~`) — this
       is different from the App ID in step 3.
    5. Edit `Configuration/AdMobConfig.cs` (gitignored, same pattern as `ApiKeys.cs`)
       and replace `BannerAdUnitId`'s value with the ad unit ID from step 4.
       (`AdMobConfig.Sample.cs` is the committed template if you ever need to recreate
       it.)
    6. Rebuild. `AdConfig.UseTestAdUnitIds` flips to `false` on its own once
       `BannerAdUnitId` no longer matches the placeholder, so no other code change is
       needed.

    **On timing — don't read a blank banner right after setup as broken:** per
    AdMob's own docs, a newly registered app/ad unit can take up to an hour to build
    up enough inventory for impressions to show reliably, and full (non-test) ad
    serving generally wants the app's store listing to be live, not just submitted.
    If the banner goes blank right after you switch off test ads, that's expected
    for a little while, not a sign the integration broke.

    **Build-verification honesty, same as every other item here:** this sandbox is
    still behind the same `nuget.org`-blocking network allowlist that affected the
    AppLovin integration (see items 3/4 for the same restriction hitting Nominatim/
    Open-Meteo), so `Plugin.AdMob`'s compiled assembly couldn't be downloaded or
    inspected here either. What's different this time: the plugin's own documentation
    (`Setup.md`, `Banner-ads.md`, `Configuration.md`, and its README quick-start,
    fetched directly from its GitHub repo rather than guessed) gave a MAUI-level API
    surface — `builder.UseAdMob()`, the `<admob:BannerAd AdUnitId="..." />` XAML
    control, `AdConfig.UseTestAdUnitIds` — that's much smaller and less
    binding-specific than the AppLovin surface was, so there's less room for the kind
    of member-name guess that took five rounds to shake out last time. It's still not
    the same as a real compile, though. If it doesn't build: everything AdMob-specific
    is contained to `Configuration/AdMobConfig*.cs`, `RouteWeatherPlanner.csproj`'s
    `Plugin.AdMob` reference, the `Platforms/Android/AndroidManifest.xml` additions,
    a few lines in `MauiProgram.cs`, and the `<admob:BannerAd>` element in
    `RoutePlanningPage.xaml` — paste the compiler error back for a fix, same as every
    other real error hit in this project so far.

    **Update — a real build turned up one more error, and it revealed the earlier
    AppLovin-era fix for it was misdiagnosed:** `java.lang.RuntimeException: ... Type
    androidx.compose.runtime.Immutable is defined multiple times`, thrown by D8 while
    merging two NuGet packages that both ship the same Compose runtime annotation
    class — `Xamarin.AndroidX.Compose.Runtime.Annotation.Android` and
    `...Annotation.Jvm`, both resolving to `1.10.0.1`. This is the *exact same error*
    that showed up once during the AppLovin scaffold and was fixed there with an
    `ExcludeAssets="all"` reference to the `.Jvm` variant. When `Anjo.Android.AppLovin`
    was removed for the AdMob swap, that fix was removed along with it under the
    assumption the duplicate class was something AppLovin's dependency graph pulled
    in — a reasonable guess at the time, but wrong: the identical error resurfaced on
    an AdMob-only build with no AppLovin package anywhere in the project. The real
    source is almost certainly `Plugin.AdMob`'s own Android dependencies
    (`Xamarin.GooglePlayServices.Ads.Lite` and/or `Xamarin.Google.
    UserMessagingPlatform`), not anything AppLovin ever contributed. The fix is
    reapplied in `RouteWeatherPlanner.csproj` — same `ExcludeAssets="all"` reference to
    `Xamarin.AndroidX.Compose.Runtime.Annotation.Jvm` at `1.10.0.1`. Lesson recorded
    here rather than just silently fixed: when a fix for a "package-specific" problem
    gets removed alongside that package, it's worth treating the removal itself as
    unverified until a real build confirms nothing else needed it.

15. **OSRM route-planning 403 (Forbidden)** — real runtime error, not a build issue:
    `OsrmRouteService` never set a `User-Agent` header. .NET's `HttpClient` sends none
    by default, and the public OSRM demo server (`router.project-osrm.org`) rejected a
    live route-planning request without one — surfaced in the app as "Couldn't plan
    that route. Response status code does not indicate success. 403 (Forbidden)."
    This is the identical bot-blocking policy `NominatimGeocodingService` already had
    to work around (see item 17-ish above); `OsrmRouteService` just never got the same
    treatment, since earlier testing happened to succeed without it. Fixed by
    converting `OsrmRouteService` from a primary constructor to a regular one (needed
    somewhere to run setup code) and setting the same identifying User-Agent string
    Nominatim already uses: `RouteWeatherPlannerApp/1.0 (contact:
    jeff@jeffreyhamby.com)`. Worth ruling out first if a 403 shows up again: this is
    the public OSRM demo server, not a production SLA, so it can also rate-limit or
    degrade under load independent of headers — if this fix doesn't fully resolve it,
    that's the next thing to check.

## Why two separate weather services

`IPrecipitationOverlayService` (radar tiles, ~2hr nowcast) and `IPointForecastService`
(hourly/daily forecast per waypoint) are intentionally not merged into one "weather
service." A single radar tile is meaningless for a stop 6 hours down the road, and
Azure Maps' purpose-built "Weather Along Route" endpoint only covers routes under 2
hours / 60 waypoints — it can't carry a multi-day trip on its own. Keeping these split
is what lets `WaypointForecastViewModel` scale to a long road trip. Full reasoning in
the architecture doc, §1 and §5.
