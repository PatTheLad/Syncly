using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.Blazor;
using Syncly.App;
using Syncly.UI;
using Syncly.UI.Services;
using Velopack;

namespace Syncly.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddSimpleConsole(o => o.SingleLine = true);
            logging.SetMinimumLevel(LogLevel.Information);
        });

        var syncly = SynclyApp.StartAsync(BuildOptions(args), loggerFactory)
            .GetAwaiter()
            .GetResult();

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        builder.Services.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        builder.Services.AddSingleton(syncly);
        builder.Services.AddSingleton(syncly.Workspace);
        builder.Services.AddSingleton<IAppUpdater>(CreateUpdater(loggerFactory));
        builder.Services.AddSingleton<IDeviceCamera, AlwaysAllowedCamera>();
        builder.Services.AddSingleton<IMailboxCapabilities, DesktopMailboxCapabilities>();
        builder.Services.AddSingleton<IFileAccess, DesktopFileAccess>();
        builder.Services.AddScoped<IQrScanner, JsQrScanner>();
        builder.Services.AddScoped<EditorState>();

        builder.RootComponents.Add<Syncly.UI.App>("#app");

        var app = builder.Build();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        var window = app.MainWindow
            .SetTitle("Syncly")
            .SetUseOsDefaultSize(false)
            .SetSize(1280, 840)
            .SetUseOsDefaultLocation(true)
            .SetChromeless(false)
            .SetDevToolsEnabled(true)
            .SetNotificationsEnabled(false);

        if (File.Exists(iconPath))
            window.SetIconFile(iconPath);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            loggerFactory.CreateLogger("Syncly").LogError(e.ExceptionObject as Exception, "Fatal error.");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            syncly.DisposeAsync().AsTask().GetAwaiter().GetResult();

        app.Run();
    }

    // .deb copies live under /usr/lib/syncly and can't use Velopack's self-replace layout, so they
    // get a fallback updater that downloads the new .deb and opens it in the desktop's own installer.
    private static IAppUpdater CreateUpdater(ILoggerFactory loggerFactory)
    {
        var velopack = new VelopackUpdater(loggerFactory);
        if (velopack.CanSelfUpdate || !IsDebInstall())
            return velopack;

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Syncly");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "syncly-updates");
        return new GitHubDebUpdater(http, OpenWithPackageInstallerAsync, cacheDirectory);
    }

    private static bool IsDebInstall() =>
        OperatingSystem.IsLinux() && AppContext.BaseDirectory.TrimEnd('/').Equals("/usr/lib/syncly", StringComparison.Ordinal);

    private static Task OpenWithPackageInstallerAsync(string path, CancellationToken ct)
    {
        // xdg-open hands off to the desktop's default .deb handler (e.g. GNOME Software), which
        // manages its own privilege prompt; we never invoke sudo/pkexec ourselves.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "xdg-open",
            Arguments = $"\"{path}\"",
            UseShellExecute = false,
        });
        return Task.CompletedTask;
    }

    private static SynclyOptions BuildOptions(string[] args) => new()
    {
        DataDirectory = Argument(args, "--data"),
        DisplayName = Argument(args, "--name"),
    };

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
