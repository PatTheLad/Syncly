using System.Text.RegularExpressions;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// A Proton Drive public folder link. Editor links look like
/// <c>https://drive.proton.me/urls/{token}#{password}</c> — the fragment is the URL password,
/// not an extra custom password.
/// </summary>
public sealed record ProtonShareUrl(string Token, string Password, string Original)
{
    private static readonly Regex TokenPattern = new("^[A-Za-z0-9_-]{8,}$", RegexOptions.CultureInvariant);

    public static ProtonShareUrl Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("Paste a Proton Drive folder link with Editor access.");

        var trimmed = input.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new FormatException("That does not look like a Proton Drive link.");

        var host = uri.Host;
        var isProton = host.Equals("drive.proton.me", StringComparison.OrdinalIgnoreCase)
                       || host.Equals("proton.me", StringComparison.OrdinalIgnoreCase)
                       || host.EndsWith(".proton.me", StringComparison.OrdinalIgnoreCase);
        if (!isProton)
            throw new FormatException("The link must be a Proton Drive share URL.");

        var token = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? "";
        if (!TokenPattern.IsMatch(token))
            throw new FormatException("The Proton Drive link is missing its share token.");

        var password = uri.Fragment.TrimStart('#');
        var extra = password.IndexOf('#');
        if (extra >= 0)
            password = password[..extra];

        password = Uri.UnescapeDataString(password).Trim();
        if (string.IsNullOrWhiteSpace(password))
            throw new FormatException(
                "The link is missing the secret after #. Copy it from Proton Drive with Editor access turned on.");

        return new ProtonShareUrl(token, password, trimmed);
    }
}

public sealed class ProtonDriveException(string message, Exception? inner = null)
    : Exception(message, inner);

[Flags]
public enum ProtonLinkFlags
{
    None = 0,
    CustomPassword = 1,
}

public enum ProtonMemberRole
{
    Viewer = 4,
    Editor = 6,
    Admin = 54,
}
