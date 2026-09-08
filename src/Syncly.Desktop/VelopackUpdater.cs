using Microsoft.Extensions.Logging;
using Syncly.App;
using Velopack;
using Velopack.Sources;

namespace Syncly.Desktop;

/// <summary>Velopack silent update: download the new package, then restart into it.</summary>
public sealed class VelopackUpdater : IAppUpdater
{
    private readonly UpdateManager? _manager;
    private readonly ILogger _logger;
    private UpdateInfo? _pending;

    public VelopackUpdater(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<VelopackUpdater>();
        try
        {
            _manager = new UpdateManager(new GithubSource(AppRelease.GitHubUrl, null, prerelease: false));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Velopack is not available in this build.");
        }
    }

    public string CurrentVersion => AppRelease.CurrentVersion;

    public bool CanSelfUpdate => _manager?.IsInstalled == true;

    public AppUpdate? Available { get; private set; }

    public string? Progress { get; private set; }

    public bool Busy { get; private set; }

    public event Action? Changed;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (_manager is not { IsInstalled: true })
            return;

        try
        {
            _pending = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
            if (_pending is null)
            {
                Available = null;
                Progress = null;
            }
            else
            {
                Available = new AppUpdate(
                    _pending.TargetFullRelease.Version.ToString(),
                    null,
                    null,
                    null);
                Progress = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed.");
            Progress = ex.Message;
        }

        Changed?.Invoke();
    }

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        if (_manager is not { IsInstalled: true } || _pending is null)
            return;

        Busy = true;
        Progress = "Downloading…";
        Changed?.Invoke();

        try
        {
            await _manager.DownloadUpdatesAsync(
                _pending,
                p =>
                {
                    Progress = $"Downloading… {p}%";
                    Changed?.Invoke();
                },
                ct);

            Progress = "Restarting…";
            Changed?.Invoke();
            _manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Silent update failed.");
            Progress = ex.Message;
            Busy = false;
            Changed?.Invoke();
        }
    }
}
