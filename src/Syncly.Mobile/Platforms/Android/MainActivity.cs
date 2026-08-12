using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Syncly.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private static readonly string[] NearbyPermissions =
    [
        Android.Manifest.Permission.AccessFineLocation,
        Android.Manifest.Permission.NearbyWifiDevices,
    ];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Wi-Fi peer discovery is gated behind these on modern Android.
        RequestPermissions(NearbyPermissions, 1);
    }
}
