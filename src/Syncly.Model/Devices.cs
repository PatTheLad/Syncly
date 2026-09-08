namespace Syncly.Model;

/// <summary>Stable identity of a device. The id is a hash of the long-lived key; the name is cosmetic.</summary>
public sealed record DeviceDescriptor(string DeviceId, string DisplayName, string PublicKeyBase64)
{
    public string ShortFingerprint => DeviceId.Length <= 8 ? DeviceId : DeviceId[..8];
}

public sealed record TrustedDevice(
    string DeviceId,
    string DisplayName,
    string PublicKeyBase64,
    DateTimeOffset TrustedAt);

public enum SyncBackendKind
{
    None = 0,
    Folder = 1,
    Cloud = 2,
}

public enum CloudProvider
{
    ProtonDrive = 0,
}

public sealed class SyncPreferences
{
    public SyncBackendKind Backend { get; set; } = SyncBackendKind.None;

    public string? FolderPath { get; set; }

    public CloudProvider Provider { get; set; } = CloudProvider.ProtonDrive;

    public string? ProtonShareUrl { get; set; }

    public bool HasMailbox => Backend switch
    {
        SyncBackendKind.Folder => !string.IsNullOrWhiteSpace(FolderPath),
        SyncBackendKind.Cloud => Provider == CloudProvider.ProtonDrive
                                 && !string.IsNullOrWhiteSpace(ProtonShareUrl),
        _ => false,
    };
}

public enum SyncPhase
{
    Disabled,
    Idle,
    Syncing,
    Failed,
}

public sealed record SyncStatus(
    SyncPhase Phase,
    string Summary,
    string? Detail,
    string? BackendName,
    DateTimeOffset? LastSuccessAt,
    int RemoteDevices);
