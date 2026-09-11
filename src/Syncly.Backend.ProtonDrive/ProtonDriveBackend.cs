using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Syncly.Sync;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// Shared folder on Proton Drive, opened from a public Editor link. Syncly blobs are already
/// ciphertext; Proton wraps them again in its own file encryption.
/// </summary>
public sealed class ProtonDriveBackend : ISyncBackend, IAsyncDisposable
{
    public const string ApiBase = "https://mail.proton.me/api/drive/";

    /// <summary>
    /// Proton's Drive API is PascalCase. <c>PostAsJsonAsync</c> without options
    /// uses camelCase and drops required fields such as <c>LinkIDs</c>.
    /// </summary>
    internal static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Public-link sessions have no <c>full</c>/<c>nondelinquent</c> scope.
    /// Volume routes must go through <c>drive/unauth/</c>; <c>drive/urls/</c> stays as-is.
    /// </summary>
    public static string DataPath(string path)
    {
        path = path.TrimStart('/');
        if (path.StartsWith("urls/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("v2/urls/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("unauth/", StringComparison.OrdinalIgnoreCase))
            return path;

        return "unauth/" + path;
    }

    private readonly ProtonShareUrl _url;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private ProtonSession? _session;

    public ProtonDriveBackend(string shareUrl, string? customPassword = null, HttpMessageHandler? handler = null)
        : this(ProtonShareUrl.Parse(shareUrl, customPassword), handler)
    {
    }

    public ProtonDriveBackend(ProtonShareUrl url, HttpMessageHandler? handler = null)
    {
        _url = url;
        if (handler is null)
        {
            _http = CreateClient();
            _ownsHttp = true;
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(ApiBase) };
            _ownsHttp = true;
            ApplyHeaders(_http);
        }
    }

    public string Name => "Proton Drive";

    public ProtonShareUrl ShareUrl => _url;

    public async Task TestAsync(CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        if (!session.CanEdit)
            throw new ProtonDriveException(
                "This Proton Drive link is view-only. In Proton Drive, share the folder again and set access to Editor.");

        await session.ListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        return await session.ListAsync(ct);
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        return await session.ReadAsync(name, ct);
    }

    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        if (!session.CanEdit)
            throw new ProtonDriveException(
                "This Proton Drive link is view-only. Recreate it with Editor access.");

        await session.WriteAsync(name, data, ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        if (!session.CanEdit)
            throw new ProtonDriveException(
                "This Proton Drive link is view-only. Recreate it with Editor access.");

        await session.DeleteAsync(name, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _session?.Dispose();
        if (_ownsHttp)
            _http.Dispose();
        await Task.CompletedTask;
    }

    internal static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(ApiBase),
            Timeout = TimeSpan.FromMinutes(10),
        };
        ApplyHeaders(http);
        return http;
    }

    private static void ApplyHeaders(HttpClient http)
    {
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Proton expects external clients as external-drive@version (see Drive API notes).
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-pm-appversion", "external-drive@2.0.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-pm-uid", "0");
    }

    private async Task<ProtonSession> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is { } open)
            return open;

        _session = await ProtonSession.OpenAsync(_http, _url, ct);
        return _session;
    }
}

internal sealed class ProtonSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _uid;
    private readonly string _accessToken;
    private readonly ProtonMailbox _mailbox;

    private ProtonSession(
        HttpClient http,
        string uid,
        string accessToken,
        bool canEdit,
        ProtonMailbox mailbox)
    {
        _http = http;
        _uid = uid;
        _accessToken = accessToken;
        CanEdit = canEdit;
        _mailbox = mailbox;
    }

    public bool CanEdit { get; }

    public static async Task<ProtonSession> OpenAsync(
        HttpClient http,
        ProtonShareUrl url,
        CancellationToken ct)
    {
        var info = await http.GetFromJsonAsync<ProtonInfoResponse>($"urls/{url.Token}/info", Json, ct)
                   ?? throw new ProtonDriveException("Proton Drive did not return share info.");

        if (info.Code is not (0 or 1000))
            throw new ProtonDriveException(info.Error ?? "Proton Drive rejected this link.");

        var needsCustom = (info.Flags & (uint)ProtonLinkFlags.CustomPassword) != 0;
        if (url.RequiresCustomPassword(info.Flags))
            throw new ProtonDriveException(
                "This Proton Drive link has an extra password. Enter it in Settings under the share URL.");

        var authPassword = url.ResolveAuthPassword(info.Flags);

        ProtonSrp.Proof auth;
        try
        {
            auth = ProtonSrp.Prove(authPassword, info);
        }
        catch (Exception ex) when (ex is not ProtonDriveException)
        {
            throw new ProtonDriveException("Could not unlock this Proton Drive link.", ex);
        }
        var request = new HttpRequestMessage(HttpMethod.Post, $"urls/{url.Token}/auth")
        {
            Content = JsonContent.Create(new ProtonAuthRequest(
                auth.ClientProof, auth.ClientEphemeral, info.SrpSession), options: Json),
        };

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadFromJsonAsync<ProtonAuthResponse>(Json, ct)
                   ?? throw new ProtonDriveException("Proton Drive authentication returned nothing.");

        if (!response.IsSuccessStatusCode || body.Code is not (0 or 1000))
        {
            var hint = needsCustom
                ? "Check the share password in Settings and that the URL still includes the secret after #."
                : "Check the password in the URL (the part after #).";
            throw new ProtonDriveException(body.Error ?? $"Could not unlock this Proton Drive link. {hint}");
        }

        if (!ProtonSrp.VerifyServer(auth, body.ServerProof))
            throw new ProtonDriveException("Proton Drive server proof did not match. The link may be forged.");

        var canEdit = body.Share.PublicPermissions >= (int)ProtonMemberRole.Editor;
        var mailbox = ProtonMailbox.Unlock(body.Share, authPassword);
        mailbox.Bind(http, url.Token, body.Uid, body.AccessToken, body.Share.VolumeId, body.Share.LinkId);

        var session = new ProtonSession(http, body.Uid, body.AccessToken, canEdit, mailbox);
        session.ApplyAuth();
        return session;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => _mailbox.ListAsync(ct);

    public Task<byte[]?> ReadAsync(string name, CancellationToken ct) => _mailbox.ReadAsync(name, ct);

    public Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        _mailbox.WriteAsync(name, data, ct);

    public Task DeleteAsync(string name, CancellationToken ct) => _mailbox.DeleteAsync(name, ct);

    public void Dispose() { }

    private void ApplyAuth()
    {
        _http.DefaultRequestHeaders.Remove("x-pm-uid");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-pm-uid", _uid);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class ProtonInfoResponse
{
    public int Code { get; set; }
    public string? Error { get; set; }
    public byte Version { get; set; }
    public string Modulus { get; set; } = "";
    public string ServerEphemeral { get; set; } = "";
    public string UrlPasswordSalt { get; set; } = "";

    [JsonPropertyName("SRPSession")]
    public string SrpSession { get; set; } = "";

    public uint Flags { get; set; }
}

internal sealed class ProtonAuthRequest(string clientProof, string clientEphemeral, string srpSession)
{
    [JsonPropertyName("ClientProof")]
    public string ClientProof { get; } = clientProof;

    [JsonPropertyName("ClientEphemeral")]
    public string ClientEphemeral { get; } = clientEphemeral;

    [JsonPropertyName("SRPSession")]
    public string SrpSession { get; } = srpSession;
}

internal sealed class ProtonAuthResponse
{
    public int Code { get; set; }
    public string? Error { get; set; }
    public string ServerProof { get; set; } = "";
    public string Uid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public ProtonShareDto Share { get; set; } = new();
}

internal sealed class ProtonShareDto
{
    public string SharePasswordSalt { get; set; } = "";
    public string ShareKey { get; set; } = "";
    public string SharePassphrase { get; set; } = "";
    public int PublicPermissions { get; set; }
    public string VolumeId { get; set; } = "";
    public string LinkId { get; set; } = "";
}
