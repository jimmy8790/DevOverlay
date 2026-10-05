namespace DevOverlay.Updates;

// Canonical public repository (git remote "origin"). Release links are built from this and a validated tag,
// never taken from a response payload.
internal static class AppLinks
{
    internal const string ProductName = "DevOverlay";
    internal const string Owner = "jimmy8790";
    internal const string Repository = "DevOverlay";
    internal const string RepositoryUrl = "https://github.com/jimmy8790/DevOverlay";
    internal const string ReleasesUrl = RepositoryUrl + "/releases";

    internal static string ReleasePageUrl(ReleaseVersion version) => $"{ReleasesUrl}/tag/v{version.Major}.{version.Minor}.{version.Patch}" +
        (version.PreRelease is null ? "" : "-" + version.PreRelease);

    // Only HTTPS links to this repository may be handed to the system browser.
    internal static bool IsAllowedBrowserUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0].Equals(Owner, StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals(Repository, StringComparison.OrdinalIgnoreCase);
    }
}
