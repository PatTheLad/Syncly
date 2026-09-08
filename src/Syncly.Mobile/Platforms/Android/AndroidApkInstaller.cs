using Android.Content;
using Android.Provider;
using AndroidX.Core.Content;
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

        if (OperatingSystem.IsAndroidVersionAtLeast(26)
            && activity.PackageManager is { CanRequestPackageInstalls: false })
        {
            var settings = new Intent(Settings.ActionManageUnknownAppSources);
            settings.SetData(Uri.Parse("package:" + activity.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(settings);
            throw new InvalidOperationException("Allow Syncly to install packages, then tap Update again.");
        }

        var file = new File(apkPath);
        var uri = FileProvider.GetUriForFile(
            activity,
            activity.PackageName + ".fileProvider",
            file);

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(intent);
        return Task.CompletedTask;
    }
}
