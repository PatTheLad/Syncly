using Microsoft.Extensions.Logging;
using Syncly.App;
using Syncly.Transport.Lan;
using Syncly.Transport.WifiDirect;
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

        // Android hands us a private app directory; everything else matches the desktop host.
        var syncly = SynclyApp.StartAsync(
                new SynclyOptions
                {
                    DataDirectory = FileSystem.AppDataDirectory,
                    DisplayName = DeviceInfo.Name,
                    Transports = () => [new LanTcpTransport()],
                    Discoveries = () => [new LanDiscovery(), new WifiDirectDiscovery("Android")],
                })
            .GetAwaiter()
            .GetResult();

        builder.Services.AddSingleton(syncly);
        builder.Services.AddSingleton(syncly.Workspace);
        builder.Services.AddScoped<EditorState>();

        return builder.Build();
    }
}
