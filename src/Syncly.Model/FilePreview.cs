namespace Syncly.Model;

/// <summary>How a File block should render in the editor.</summary>
public enum FilePreviewKind
{
    Card,
    Image,
    Video,
    Pdf,
}

public static class FilePreview
{
    public static FilePreviewKind ForMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return FilePreviewKind.Card;

        var value = mime.Trim().ToLowerInvariant();
        if (value.StartsWith("image/", StringComparison.Ordinal))
            return FilePreviewKind.Image;
        if (value.StartsWith("video/", StringComparison.Ordinal))
            return FilePreviewKind.Video;
        if (value is "application/pdf")
            return FilePreviewKind.Pdf;

        return FilePreviewKind.Card;
    }

    public static string GuessMime(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }
}
