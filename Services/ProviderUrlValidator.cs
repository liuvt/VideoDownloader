namespace VideoDownloader.Blazor.Services;

public sealed class ProviderUrlValidator
{
    public bool TryValidate(string? value, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Paste a YouTube or Facebook video URL.";
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length > 2048)
        {
            error = "The URL is too long.";
            return false;
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"https://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            error = "Enter a valid web address.";
            return false;
        }

        var host = parsed.IdnHost.TrimEnd('.');
        if (!IsSupportedHost(host))
        {
            error = "This version supports YouTube and Facebook links only.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsSupportedHost(string host) =>
        IsHostOrSubdomain(host, "youtube.com") ||
        IsHostOrSubdomain(host, "youtube-nocookie.com") ||
        host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        IsHostOrSubdomain(host, "facebook.com") ||
        IsHostOrSubdomain(host, "fb.watch");

    private static bool IsHostOrSubdomain(string host, string rootDomain) =>
        host.Equals(rootDomain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{rootDomain}", StringComparison.OrdinalIgnoreCase);
}
