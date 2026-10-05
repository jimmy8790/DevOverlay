using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DevOverlay.Updates;

internal enum UpdateFailure { None, Offline, Timeout, RateLimited, HttpStatus, MalformedResponse, NoRelease, UnknownInstalledVersion }

internal sealed record ReleaseInfo(string Tag, bool Draft, bool PreRelease);

internal sealed record ReleaseFetchResult(IReadOnlyList<ReleaseInfo> Releases, UpdateFailure Failure = UpdateFailure.None, int? StatusCode = null)
{
    internal static ReleaseFetchResult Fail(UpdateFailure failure, int? statusCode = null) => new([], failure, statusCode);
}

internal interface IReleaseSource
{
    Task<ReleaseFetchResult> FetchAsync(CancellationToken cancellationToken);
}

// Public, unauthenticated GitHub Releases list. Sends nothing but the request line, Accept and User-Agent.
internal sealed class GitHubReleaseSource : IReleaseSource, IDisposable
{
    internal static readonly Uri Endpoint = new($"https://api.github.com/repos/{AppLinks.Owner}/{AppLinks.Repository}/releases?per_page=30");
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxResponseBytes = 1_000_000;
    private readonly HttpClient _client;

    internal GitHubReleaseSource(string userAgentVersion, HttpMessageHandler? handler = null)
    {
        _client = handler is null
            ? new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = Timeout.InfiniteTimeSpan; // per-request timeout below so cancellation and timeout stay distinguishable
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppLinks.ProductName, userAgentVersion));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ReleaseFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _client.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return ReleaseFetchResult.Fail(UpdateFailure.NoRelease, 404);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
                return ReleaseFetchResult.Fail(UpdateFailure.RateLimited, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode) return ReleaseFetchResult.Fail(UpdateFailure.HttpStatus, (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > MaxResponseBytes) return ReleaseFetchResult.Fail(UpdateFailure.MalformedResponse);
            var bytes = await ReadLimitedAsync(response, timeout.Token).ConfigureAwait(false);
            return bytes is null ? ReleaseFetchResult.Fail(UpdateFailure.MalformedResponse) : Parse(bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ReleaseFetchResult.Fail(UpdateFailure.Timeout); }
        catch (HttpRequestException) { return ReleaseFetchResult.Fail(UpdateFailure.Offline); }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static ReleaseFetchResult Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Array) return ReleaseFetchResult.Fail(UpdateFailure.MalformedResponse);
            var releases = new List<ReleaseInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("tag_name", out var tag) ||
                    tag.ValueKind != JsonValueKind.String) continue;
                releases.Add(new(tag.GetString()!, Flag(item, "draft"), Flag(item, "prerelease")));
            }
            return new(releases);
        }
        catch (JsonException) { return ReleaseFetchResult.Fail(UpdateFailure.MalformedResponse); }
    }

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // Highest stable version among published, non-draft, non-pre-release entries with a well-formed tag.
    internal static ReleaseVersion? LatestStable(IEnumerable<ReleaseInfo> releases) => releases
        .Where(release => !release.Draft && !release.PreRelease)
        .Select(release => ReleaseVersion.TryParse(release.Tag))
        .Where(version => version is { IsPreRelease: false })
        .Select(version => version!.Value)
        .Order()
        .Select(version => (ReleaseVersion?)version)
        .LastOrDefault();

    public void Dispose() => _client.Dispose();
}
