namespace Syncly.App;

/// <summary>Host-specific mailbox features (folder paths need a real filesystem).</summary>
public interface IMailboxCapabilities
{
    bool SupportsLocalFolder { get; }
}

public sealed class DesktopMailboxCapabilities : IMailboxCapabilities
{
    public bool SupportsLocalFolder => true;
}

public sealed class MobileMailboxCapabilities : IMailboxCapabilities
{
    public bool SupportsLocalFolder => false;
}
