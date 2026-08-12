using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.Blazor;
using Syncly.Contracts.Abstractions;
using Syncly.Core;
using Syncly.Platform.Lan;
using Syncly.UI.Services;

namespace Syncly.App.Linux;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var dataDir = Environment.GetEnvironmentVariable("SYNCLY_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Syncly");

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        builder.Services.AddLogging(l => l.AddConsole().SetMinimumLevel(LogLevel.Information));
        builder.Services.AddSynclyCore(o =>
        {
            o.DataDirectory = dataDir;
            o.ListenPort = ResolvePort(args);
        });
        builder.Services.AddSynclyLanTransport();
        builder.Services.AddSingleton<InteractivePairingPrompter>();
        builder.Services.AddSingleton<IPairingPrompter>(sp => sp.GetRequiredService<InteractivePairingPrompter>());
        builder.Services.AddSingleton<AppState>();

        builder.RootComponents.Add<Syncly.UI.App>("app");

        var app = builder.Build();

        var host = app.Services.GetRequiredService<SyncHostService>();
        host.InitializeAsync().GetAwaiter().GetResult();

        app.MainWindow
            .SetTitle("Syncly")
            .SetUseOsDefaultSize(false)
            .SetUseOsDefaultLocation(true)
            .SetWidth(1100)
            .SetHeight(760);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine(e.ExceptionObject);

        app.Run();
    }

    private static int ResolvePort(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var port))
                return port;
        }

        var env = Environment.GetEnvironmentVariable("SYNCLY_PORT");
        if (int.TryParse(env, out var fromEnv))
            return fromEnv;

        return Syncly.Contracts.Models.SynclyConstants.DefaultPort;
    }
}
