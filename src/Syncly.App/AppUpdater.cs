using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Syncly.App;

public static class AppRelease
{
    public const string GitHubOwner = "PatTheLad";
    public const string GitHubRepo = "Syncly";
    public const string GitHubUrl = "https://github.com/PatTheLad/Syncly";

    public static string CurrentVersion { get; } =
        typeof(AppRelease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+', StringSplitOptions.TrimEntries)[0]
        ?? typeof(AppRelease).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string LatestReleaseApi =>
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
}

public sealed record AppUpdate(string Version, string? Notes, string? DownloadUrl, string? AssetName);

public interface IAppUpdater
{
    string CurrentVersion { get; }

    /// <summary>False for `dotnet run` and other unpackaged builds that cannot self-replace.</summary>
    bool CanSelfUpdate { get; }

    AppUpdate? Available { get; }

    string? Progress { get; }

    bool Busy { get; }

    event Action? Changed;

    Task CheckAsync(CancellationToken ct = default);

    Task ApplyAsync(CancellationToken ct = default);
}

public sealed class NoOpAppUpdater : IAppUpdater
{
    public string CurrentVersion => AppRelease.CurrentVersion;

    public bool CanSelfUpdate => false;

    public AppUpdate? Available => null;

    public string? Progress => null;

    public bool Busy => false;

    public event Action? Changed { add { } remove { } }

    public Task CheckAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ApplyAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public static class ReleaseVersion
{
    public static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var plus = text.IndexOfAny(['+', '-']);
        if (plus >= 0)
            text = text[..plus];

        return Version.TryParse(text, out version!);
    }

    public static bool IsNewer(string? latestTag, string current)
    {
        if (!TryParse(latestTag, out var latest) || !TryParse(current, out var here))
            return false;

        return latest > here;
    }
}

public sealed class GitHubReleaseClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<GitHubRelease?> GetLatestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AppRelease.LatestReleaseApi);
        request.Headers.TryAddWithoutValidation("User-Agent", "Syncly");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<GitHubRelease>(Json, ct);
    }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];

    public GitHubAsset? FindAsset(Func<string, bool> match) =>
        Assets.FirstOrDefault(a => match(a.Name));
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>Android (and any host that installs a downloaded APK) uses this to apply a GitHub release.</summary>
public sealed class GitHubApkUpdater : IAppUpdater
{
    private readonly GitHubReleaseClient _client;
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task> _install;
    private readonly string _cacheDirectory;

    public GitHubApkUpdater(
        HttpClient http,
        Func<string, CancellationToken, Task> install,
        string cacheDirectory)
    {
        _http = http;
        _client = new GitHubReleaseClient(http);
        _install = install;
        _cacheDirectory = cacheDirectory;
    }

    public string CurrentVersion => AppRelease.CurrentVersion;

    public bool CanSelfUpdate => true;

    public AppUpdate? Available { get; private set; }

    public string? Progress { get; private set; }

    public bool Busy { get; private set; }

    public event Action? Changed;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var release = await _client.GetLatestAsync(ct);
            if (release is null || !ReleaseVersion.IsNewer(release.TagName, CurrentVersion))
            {
                Available = null;
                Progress = null;
                Changed?.Invoke();
                return;
            }

            var apk = release.FindAsset(name =>
                name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                && name.Contains("android", StringComparison.OrdinalIgnoreCase))
                ?? release.FindAsset(name => name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

            if (apk is null)
            {
                Available = null;
                Progress = "A newer release exists, but it has no Android package.";
                Changed?.Invoke();
                return;
            }

            Available = new AppUpdate(release.TagName.TrimStart('v', 'V'), release.Body, apk.BrowserDownloadUrl, apk.Name);
            Progress = null;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Progress = ex.Message;
            Changed?.Invoke();
        }
    }

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        if (Available?.DownloadUrl is not { Length: > 0 } url)
            return;

        Busy = true;
        Progress = "Downloading…";
        Changed?.Invoke();

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = Path.Combine(_cacheDirectory, Available.AssetName ?? "Syncly.apk");

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[64 * 1024];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                    Progress = $"Downloading… {read * 100 / total}%";
                Changed?.Invoke();
            }

            Progress = "Installing…";
            Changed?.Invoke();
            await _install(path, ct);
            Progress = "Installer started.";
        }
        catch (Exception ex)
        {
            Progress = ex.Message;
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }
}
