using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.Blazor;
using Syncly.App;
using Syncly.Sync;
using Syncly.Transport.Lan;
using Syncly.Transport.WifiDirect;
using Syncly.UI;
using Syncly.UI.Services;

namespace Syncly.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddSimpleConsole(o => o.SingleLine = true);
            logging.SetMinimumLevel(LogLevel.Information);
        });

        // The whole app hangs off one composition root, so it is started before the UI exists and
        // handed to DI as a ready object.
        var syncly = SynclyApp.StartAsync(BuildOptions(args), loggerFactory)
            .GetAwaiter()
            .GetResult();

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        builder.Services.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        builder.Services.AddSingleton(syncly);
        builder.Services.AddSingleton(syncly.Workspace);
        builder.Services.AddScoped<EditorState>();

        builder.RootComponents.Add<Syncly.UI.App>("#app");

        var app = builder.Build();

        app.MainWindow
            .SetTitle("Syncly")
            .SetUseOsDefaultSize(false)
            .SetSize(1280, 840)
            .SetUseOsDefaultLocation(true)
            .SetChromeless(false)
            .SetDevToolsEnabled(true);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            loggerFactory.CreateLogger("Syncly").LogError(e.ExceptionObject as Exception, "Fatal error.");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            syncly.DisposeAsync().AsTask().GetAwaiter().GetResult();

        app.Run();
    }

    private static SynclyOptions BuildOptions(string[] args) => new()
    {
        DataDirectory = Argument(args, "--data"),
        DisplayName = Argument(args, "--name"),
        ListenPort = int.TryParse(Argument(args, "--port"), out var port) ? port : 45_654,
        Transports = () => [new LanTcpTransport()],
        Discoveries = () =>
        [
            new LanDiscovery(),
            new WifiDirectDiscovery(OperatingSystem.IsWindows() ? "Windows" : "Linux"),
        ],
    };

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
