using System.Text.RegularExpressions;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// A Proton Drive public folder link. Editor links look like
/// <c>https://drive.proton.me/urls/{token}#{password}</c> — the fragment is the URL password.
/// An optional share password set in Proton Drive is stored separately and concatenated for SRP.
/// </summary>
public sealed record ProtonShareUrl(string Token, string UrlPassword, string Original, string? CustomPassword = null)
{
    /// <summary>Proton generates a 12-character URL secret; anything after that in the fragment is custom.</summary>
    public const int GeneratedPasswordLength = 12;

    private static readonly Regex TokenPattern = new("^[A-Za-z0-9_-]{8,}$", RegexOptions.CultureInvariant);

    /// <summary>URL fragment plus optional custom password — Proton's SRP / share-key secret.</summary>
    public string Password => string.IsNullOrEmpty(CustomPassword)
        ? UrlPassword
        : UrlPassword + CustomPassword;

    public static ProtonShareUrl Parse(string input, string? customPassword = null)
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

        var custom = string.IsNullOrWhiteSpace(customPassword) ? null : customPassword.Trim();
        return new ProtonShareUrl(token, password, trimmed, custom);
    }

    public ProtonShareUrl WithCustomPassword(string? customPassword) =>
        this with { CustomPassword = string.IsNullOrWhiteSpace(customPassword) ? null : customPassword.Trim() };

    /// <summary>
    /// Build the SRP / share-unlock secret the way Proton's web client does:
    /// generated URL password (12 chars when that flag is set) + optional custom password.
    /// </summary>
    public string ResolveAuthPassword(uint flags)
    {
        var generatedIncluded = (flags & (uint)ProtonLinkFlags.GeneratedPasswordIncluded) != 0;
        var needsCustom = (flags & (uint)ProtonLinkFlags.CustomPassword) != 0;

        string urlPart;
        string? fromFragment = null;
        if (generatedIncluded && UrlPassword.Length > GeneratedPasswordLength)
        {
            urlPart = UrlPassword[..GeneratedPasswordLength];
            fromFragment = UrlPassword[GeneratedPasswordLength..];
        }
        else
            urlPart = UrlPassword;

        // Prefer the Settings field; fall back to a custom suffix already embedded after the 12-char secret.
        var custom = CustomPassword ?? fromFragment;

        if (needsCustom && !generatedIncluded)
        {
            // Legacy CustomPassword-only shares: the password the user typed is the whole secret.
            if (!string.IsNullOrEmpty(CustomPassword))
                return CustomPassword;
            return urlPart;
        }

        if (string.IsNullOrEmpty(custom))
            return urlPart;

        return urlPart + custom;
    }

    public bool RequiresCustomPassword(uint flags)
    {
        if ((flags & (uint)ProtonLinkFlags.CustomPassword) == 0)
            return false;

        if ((flags & (uint)ProtonLinkFlags.GeneratedPasswordIncluded) != 0
            && UrlPassword.Length > GeneratedPasswordLength)
            return false;

        return string.IsNullOrEmpty(CustomPassword);
    }
}

public sealed class ProtonDriveException(string message, Exception? inner = null)
    : Exception(message, inner);

[Flags]
public enum ProtonLinkFlags
{
    None = 0,
    CustomPassword = 1,
    GeneratedPasswordIncluded = 2,
}

public enum ProtonMemberRole
{
    Viewer = 4,
    Editor = 6,
    Admin = 54,
}
