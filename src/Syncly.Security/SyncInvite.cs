using System.Text.Json;
using System.Text.Json.Serialization;
using Syncly.Model;

namespace Syncly.Security;

/// <summary>
/// One-scan handoff: chain secret plus mailbox settings (Proton link/password or folder path).
/// Encoded as <c>syncly:invite:v1:</c> + base64url(JSON).
/// </summary>
public sealed class SyncInvite
{
    public const string UriPrefix = "syncly:invite:v1:";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public required string ChainUri { get; init; }

    public SyncBackendKind Backend { get; init; }

    public string? ProtonShareUrl { get; init; }

    public string? ProtonSharePassword { get; init; }

    public string? FolderPath { get; init; }

    public static SyncInvite From(SyncChain chain, SyncPreferences preferences) => new()
    {
        ChainUri = chain.Uri,
        Backend = preferences.Backend,
        ProtonShareUrl = EmptyToNull(preferences.ProtonShareUrl),
        ProtonSharePassword = EmptyToNull(preferences.ProtonSharePassword),
        FolderPath = EmptyToNull(preferences.FolderPath),
    };

    public string ToUri()
    {
        var dto = new InviteDto
        {
            C = ChainUri.StartsWith(SyncChain.UriPrefix, StringComparison.OrdinalIgnoreCase)
                ? ChainUri[SyncChain.UriPrefix.Length..]
                : ChainUri,
            B = Backend switch
            {
                SyncBackendKind.Folder => "folder",
                SyncBackendKind.Cloud => "cloud",
                _ => null,
            },
            U = ProtonShareUrl,
            P = ProtonSharePassword,
            F = FolderPath,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        return UriPrefix + Base64Url.Encode(json);
    }

    public void ApplyTo(SyncPreferences preferences, bool supportsLocalFolder = true)
    {
        if (Backend != SyncBackendKind.None)
            preferences.Backend = Backend;

        if (ProtonShareUrl is not null)
        {
            preferences.ProtonShareUrl = ProtonShareUrl;
            preferences.Provider = CloudProvider.ProtonDrive;
        }

        if (ProtonSharePassword is not null)
            preferences.ProtonSharePassword = ProtonSharePassword;

        if (FolderPath is not null)
            preferences.FolderPath = FolderPath;

        if (!supportsLocalFolder)
            StripLocalFolder(preferences);
    }

    /// <summary>Folder paths are desktop-only; keep Proton/cloud fields when present.</summary>
    public static void StripLocalFolder(SyncPreferences preferences)
    {
        preferences.FolderPath = null;
        if (preferences.Backend != SyncBackendKind.Folder)
            return;

        preferences.Backend = string.IsNullOrWhiteSpace(preferences.ProtonShareUrl)
            ? SyncBackendKind.None
            : SyncBackendKind.Cloud;
    }

    public static bool TryParse(string input, out SyncInvite invite)
    {
        try
        {
            invite = Parse(input);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException)
        {
            invite = null!;
            return false;
        }
    }

    public static SyncInvite Parse(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Not a Syncly invite QR.");

        var payload = Base64Url.Decode(trimmed[UriPrefix.Length..]);
        var dto = JsonSerializer.Deserialize<InviteDto>(payload, Json)
                  ?? throw new FormatException("Invite QR is empty.");

        if (string.IsNullOrWhiteSpace(dto.C))
            throw new FormatException("Invite QR is missing the chain.");

        var chainUri = dto.C.StartsWith(SyncChain.UriPrefix, StringComparison.OrdinalIgnoreCase)
            ? dto.C
            : SyncChain.UriPrefix + dto.C;

        // Validate the chain payload early so a bad QR fails before mailbox prefs are applied.
        _ = SyncChain.Parse(chainUri);

        var backend = dto.B?.ToLowerInvariant() switch
        {
            "folder" => SyncBackendKind.Folder,
            "cloud" => SyncBackendKind.Cloud,
            _ => string.IsNullOrWhiteSpace(dto.U) ? SyncBackendKind.None : SyncBackendKind.Cloud,
        };

        return new SyncInvite
        {
            ChainUri = chainUri,
            Backend = backend,
            ProtonShareUrl = EmptyToNull(dto.U),
            ProtonSharePassword = EmptyToNull(dto.P),
            FolderPath = EmptyToNull(dto.F),
        };
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class InviteDto
    {
        public string? C { get; set; }
        public string? B { get; set; }
        public string? U { get; set; }
        public string? P { get; set; }
        public string? F { get; set; }
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = Convert.ToBase64String(data);
        return text.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Invite QR payload is not valid base64url.", ex);
        }
    }
}
