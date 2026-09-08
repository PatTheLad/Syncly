using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidXFileProvider = AndroidX.Core.Content.FileProvider;
using File = Java.IO.File;
using Uri = Android.Net.Uri;

namespace Syncly.Mobile;

internal sealed class AndroidApkInstaller
{
    public Task InstallAsync(string apkPath, CancellationToken ct = default)
    {
        _ = ct;
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                       ?? throw new InvalidOperationException("Syncly is not in the foreground.");

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O
            && activity.PackageManager is { } pm
            && !pm.CanRequestPackageInstalls())
        {
            var settings = new Intent(Settings.ActionManageUnknownAppSources);
            settings.SetData(Uri.Parse("package:" + activity.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(settings);
            throw new InvalidOperationException(
                "Allow Syncly to install updates in system settings, then tap Update again.");
        }

        var uri = AndroidXFileProvider.GetUriForFile(
            activity,
            activity.PackageName + ".fileProvider",
            new File(apkPath));

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(intent);
        return Task.CompletedTask;
    }
}
