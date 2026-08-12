using Microsoft.Extensions.Logging;
using Syncly.Contracts.Abstractions;
using Syncly.Core;
using Syncly.Platform.Lan;
using Syncly.UI.Services;

namespace Syncly.App.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => { });

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var dataDir = Path.Combine(FileSystem.AppDataDirectory, "Syncly");
        builder.Services.AddSynclyCore(o => o.DataDirectory = dataDir);
        builder.Services.AddSynclyLanTransport();
        builder.Services.AddSingleton<InteractivePairingPrompter>();
        builder.Services.AddSingleton<IPairingPrompter>(sp => sp.GetRequiredService<InteractivePairingPrompter>());
        builder.Services.AddSingleton<AppState>();

        var app = builder.Build();
        app.Services.GetRequiredService<SyncHostService>().InitializeAsync().GetAwaiter().GetResult();
        return app;
    }
}
