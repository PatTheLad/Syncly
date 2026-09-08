using Android.Content;
using AndroidXFileProvider = AndroidX.Core.Content.FileProvider;
using File = Java.IO.File;

namespace Syncly.Mobile;

internal sealed class AndroidApkInstaller
{
    public Task InstallAsync(string apkPath, CancellationToken ct = default)
    {
        _ = ct;
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                       ?? throw new InvalidOperationException("Syncly is not in the foreground.");

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
