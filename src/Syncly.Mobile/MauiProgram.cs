using Microsoft.Extensions.Logging;
using Syncly.App;
using Syncly.UI.Services;

namespace Syncly.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var syncly = SynclyApp.StartAsync(
                new SynclyOptions
                {
                    DataDirectory = FileSystem.AppDataDirectory,
                    DisplayName = DeviceInfo.Name,
                })
            .GetAwaiter()
            .GetResult();

        builder.Services.AddSingleton(syncly);
        builder.Services.AddSingleton(syncly.Workspace);
        builder.Services.AddScoped<EditorState>();
        builder.Services.AddSingleton<IDeviceCamera, MauiCameraPermission>();
        builder.Services.AddSingleton<IQrScanner, AndroidQrScanner>();
        builder.Services.AddSingleton<IAppUpdater>(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Syncly");
            return new GitHubApkUpdater(
                http,
                (path, ct) => new AndroidApkInstaller().InstallAsync(path, ct),
                FileSystem.CacheDirectory);
        });

        return builder.Build();
    }
}
