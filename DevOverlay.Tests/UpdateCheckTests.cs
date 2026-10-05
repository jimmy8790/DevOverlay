using System.Net;
using System.Text;
using DevOverlay.Configuration;
using DevOverlay.Presentation;
using DevOverlay.Updates;
using Xunit;

namespace DevOverlay.Tests;

public sealed class UpdateCheckTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text)!.Value;
    private static ReleaseFetchResult Releases(params (string Tag, bool Draft, bool Pre)[] items) =>
        new(items.Select(item => new ReleaseInfo(item.Tag, item.Draft, item.Pre)).ToArray());
    private static UpdateStatus Evaluate(string running, params (string Tag, bool Draft, bool Pre)[] items) =>
        UpdateChecker.Evaluate(Releases(items), V(running));

    [Fact]
    public void SameVersionIsUpToDate()
    {
        var status = Evaluate("0.2.0", ("v0.2.0", false, false));
        Assert.Equal(UpdateState.UpToDate, status.State); Assert.False(status.BuildIsNewer);
    }

    [Fact]
    public void NewerReleaseIsAnUpdateAndLinksToItsOwnReleasePage()
    {
        var status = Evaluate("0.2.0", ("v0.2.1", false, false), ("v0.2.0", false, false));
        Assert.Equal(UpdateState.UpdateAvailable, status.State);
        Assert.Equal("https://github.com/jimmy8790/DevOverlay/releases/tag/v0.2.1", status.ReleaseUrl);
        Assert.Equal(V("0.2.1"), status.Latest);
    }

    [Fact]
    public void BuildNewerThanTheLatestReleaseIsUpToDateAndMarkedAsDevelopmentBuild()
    {
        var status = Evaluate("0.3.0", ("v0.2.1", false, false));
        Assert.Equal(UpdateState.UpToDate, status.State); Assert.True(status.BuildIsNewer);
    }

    [Theory]
    [InlineData("0.2.10", "0.2.9", 1)] [InlineData("0.2.9", "0.2.10", -1)] [InlineData("v1.0.0", "0.99.99", 1)]
    [InlineData("v0.2.0", "0.2.0", 0)] [InlineData("V0.2.0", "0.2.0", 0)] [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("0.2.0", "0.2.0-beta.1", 1)] [InlineData("0.2.0-beta.2", "0.2.0-beta.10", -1)] [InlineData("0.2.0-rc.1", "0.2.0-beta.9", 1)]
    public void VersionsCompareNumericallyNotAsText(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(V(left).CompareTo(V(right))));

    [Theory]
    [InlineData("0.2")] [InlineData("v")] [InlineData("latest")] [InlineData("0.2.x")] [InlineData("1.2.3.4")]
    [InlineData("99999999.0.0")] [InlineData("v0.2.0 beta")] [InlineData("")] [InlineData(null)] [InlineData("-1.0.0")]
    public void MalformedVersionsAreRejected(string? text) => Assert.Null(ReleaseVersion.TryParse(text));

    [Theory]
    [InlineData("v0.2.1", "0.2.1")] [InlineData("0.2.1+build.5", "0.2.1")] [InlineData(" v1.0.0 ", "1.0.0")]
    public void OptionalLeadingVAndBuildMetadataAreNormalized(string text, string expected) =>
        Assert.Equal(expected, V(text).ToString());

    [Fact]
    public void DraftsPrereleasesAndMalformedTagsAreIgnored()
    {
        var status = Evaluate("0.2.0",
            ("v0.3.0", true, false), ("v0.2.5", false, true), ("v0.2.4-beta.1", false, false), ("nightly", false, false),
            ("v0.2.1", false, false));
        Assert.Equal(UpdateState.UpdateAvailable, status.State); Assert.Equal(V("0.2.1"), status.Latest);
    }

    [Fact]
    public void HighestStableVersionWinsRegardlessOfListOrder()
    {
        var status = Evaluate("0.1.0", ("v0.2.9", false, false), ("v0.2.10", false, false), ("v0.1.5", false, false));
        Assert.Equal(V("0.2.10"), status.Latest);
    }

    [Fact]
    public void NoReleasesOrOnlyUnusableOnesIsUnavailableNotAFalseUpToDate()
    {
        Assert.Equal(UpdateFailure.NoRelease, Evaluate("0.2.0").Failure);
        Assert.Equal(UpdateFailure.NoRelease, Evaluate("0.2.0", ("v0.3.0", true, false), ("oops", false, false)).Failure);
        Assert.Equal(UpdateState.Unavailable, Evaluate("0.2.0", ("v0.3.0", false, true)).State);
    }

    [Fact]
    public void UnknownInstalledVersionIsReportedInsteadOfGuessing()
    {
        var status = UpdateChecker.Evaluate(Releases(("v0.2.1", false, false)), null);
        Assert.Equal(UpdateState.Unavailable, status.State); Assert.Equal(UpdateFailure.UnknownInstalledVersion, status.Failure);
    }

    [Fact]
    public void RunningVersionComesFromBuildMetadataWithoutAFourthComponent()
    {
        var running = ReleaseVersion.Running(typeof(App).Assembly);
        Assert.NotNull(running);
        Assert.Equal(running!.Value.ToString(), new SettingsViewModel(OverlaySettings.CreateDefault()).VersionText["Version ".Length..]);
        Assert.Matches(@"^Version \d+\.\d+\.\d+$", new SettingsViewModel(OverlaySettings.CreateDefault()).VersionText);
    }

    [Theory]
    [InlineData("https://github.com/jimmy8790/DevOverlay", true)]
    [InlineData("https://github.com/jimmy8790/DevOverlay/releases", true)]
    [InlineData("https://github.com/jimmy8790/DevOverlay/releases/tag/v0.2.1", true)]
    [InlineData("https://GitHub.com/JIMMY8790/devoverlay/releases", true)]
    [InlineData("http://github.com/jimmy8790/DevOverlay", false)]
    [InlineData("https://github.com.evil.example/jimmy8790/DevOverlay", false)]
    [InlineData("https://github.com/someone-else/DevOverlay", false)]
    [InlineData("https://github.com/jimmy8790/DevOverlay-fake", false)]
    [InlineData("https://github.com/jimmy8790", false)]
    [InlineData("https://user@github.com/jimmy8790/DevOverlay", false)]
    [InlineData("https://github.com:8443/jimmy8790/DevOverlay", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("jimmy8790/DevOverlay", false)] [InlineData("", false)] [InlineData(null, false)]
    public void OnlyHttpsLinksToThisRepositoryMayOpenInTheBrowser(string? url, bool expected) =>
        Assert.Equal(expected, AppLinks.IsAllowedBrowserUrl(url));

    [Fact]
    public void ReleaseUrlsAreBuiltFromTheKnownRepositoryAndValidatedTagNeverFromThePayload()
    {
        var payload = Encoding.UTF8.GetBytes("""[{"tag_name":"v0.2.1","draft":false,"prerelease":false,"html_url":"https://evil.example/x"}]""");
        var parsed = GitHubReleaseSource.Parse(payload);
        var status = UpdateChecker.Evaluate(parsed, V("0.2.0"));
        Assert.StartsWith(AppLinks.ReleasesUrl, status.ReleaseUrl);
        Assert.True(AppLinks.IsAllowedBrowserUrl(status.ReleaseUrl));
    }

    // ---- HTTP behavior through a fake handler: no live GitHub call.
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request); return respond(request, cancellationToken); }
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static GitHubReleaseSource Source(FakeHandler handler) => new("0.1.2", handler);

    [Fact]
    public async Task SuccessfulResponseReturnsReleasesAndSendsOnlyAcceptAndUserAgent()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json("""[{"tag_name":"v0.2.1","draft":false,"prerelease":false}]""")));
        var result = await Source(handler).FetchAsync(default);
        Assert.Equal(UpdateFailure.None, result.Failure);
        Assert.Equal("v0.2.1", Assert.Single(result.Releases).Tag);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(GitHubReleaseSource.Endpoint, request.RequestUri); Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https", request.RequestUri!.Scheme); Assert.Null(request.Headers.Authorization);
        Assert.Equal("DevOverlay/0.1.2", request.Headers.UserAgent.ToString());
        Assert.Equal(["Accept", "User-Agent"], request.Headers.Select(header => header.Key).Order().ToArray());
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NoRelease")]
    [InlineData(HttpStatusCode.InternalServerError, "HttpStatus")]
    [InlineData(HttpStatusCode.BadGateway, "HttpStatus")]
    [InlineData(HttpStatusCode.Unauthorized, "HttpStatus")]
    [InlineData(HttpStatusCode.TooManyRequests, "RateLimited")]
    [InlineData(HttpStatusCode.Redirect, "HttpStatus")]
    public async Task HttpFailuresBecomeUnavailableReasons(HttpStatusCode code, string expectedName)
    {
        var result = await Source(new FakeHandler((_, _) => Task.FromResult(Json("{}", code)))).FetchAsync(default);
        Assert.Equal(Enum.Parse<UpdateFailure>(expectedName), result.Failure); Assert.Equal((int)code, result.StatusCode);
    }

    [Fact]
    public async Task ForbiddenWithExhaustedQuotaIsRateLimitedButPlainForbiddenIsAnHttpError()
    {
        var limited = Json("{}", HttpStatusCode.Forbidden); limited.Headers.Add("X-RateLimit-Remaining", "0");
        Assert.Equal(UpdateFailure.RateLimited, (await Source(new FakeHandler((_, _) => Task.FromResult(limited))).FetchAsync(default)).Failure);
        Assert.Equal(UpdateFailure.HttpStatus, (await Source(new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.Forbidden)))).FetchAsync(default)).Failure);
    }

    [Theory]
    [InlineData("not json")] [InlineData("")] [InlineData("{}")] [InlineData("null")] [InlineData("[{\"tag_name\":")]
    public async Task MalformedJsonIsReportedWithoutThrowing(string body)
    {
        var result = await Source(new FakeHandler((_, _) => Task.FromResult(Json(body)))).FetchAsync(default);
        Assert.Equal(UpdateFailure.MalformedResponse, result.Failure);
    }

    [Fact]
    public async Task ElementsWithoutAUsableTagAreSkipped()
    {
        var result = await Source(new FakeHandler((_, _) => Task.FromResult(Json("""[1,{"x":1},{"tag_name":5},{"tag_name":"v0.2.1"}]""")))).FetchAsync(default);
        Assert.Equal("v0.2.1", Assert.Single(result.Releases).Tag);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        var body = "[" + string.Join(",", Enumerable.Repeat("""{"tag_name":"v0.0.1","body":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""", 30_000)) + "]";
        Assert.Equal(UpdateFailure.MalformedResponse, (await Source(new FakeHandler((_, _) => Task.FromResult(Json(body)))).FetchAsync(default)).Failure);
    }

    [Fact]
    public async Task NetworkErrorIsOffline()
    {
        var result = await Source(new FakeHandler((_, _) => throw new HttpRequestException("dns"))).FetchAsync(default);
        Assert.Equal(UpdateFailure.Offline, result.Failure);
    }

    [Fact]
    public async Task HungRequestTimesOutAndCallerCancellationStillCancels()
    {
        var hung = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json("[]"); });
        using var cancel = new CancellationTokenSource();
        var pending = Source(hung).FetchAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        // An internal timeout (not caller cancellation) is reported as a failure value.
        var timeoutHandler = new FakeHandler((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));
        Assert.Equal(UpdateFailure.Timeout, (await Source(timeoutHandler).FetchAsync(default)).Failure);
    }

    // ---- Caching, manual refresh, in-flight handling and stale results.
    private sealed class FakeSource : IReleaseSource
    {
        internal int Calls;
        internal Func<CancellationToken, Task<ReleaseFetchResult>> Respond = _ => Task.FromResult(Releases(("v0.2.1", false, false)));
        public Task<ReleaseFetchResult> FetchAsync(CancellationToken token) { Interlocked.Increment(ref Calls); return Respond(token); }
    }
    private sealed class Clock { internal DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero); internal DateTimeOffset Read() => Now; }

    [Fact]
    public async Task FreshCacheIsReusedAndStaleCacheIsRefreshed()
    {
        var source = new FakeSource(); var clock = new Clock();
        var checker = new UpdateChecker(source, V("0.2.0"), clock.Read);
        Assert.Equal(UpdateState.UpdateAvailable, (await checker.CheckAsync(false)).State);
        clock.Now += TimeSpan.FromHours(5);
        Assert.Equal(UpdateState.UpdateAvailable, (await checker.CheckAsync(false)).State);
        Assert.Equal(1, source.Calls);
        clock.Now += TimeSpan.FromHours(2);
        await checker.CheckAsync(false);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task FailuresAreCachedBrieflySoAnOfflineSettingsWindowDoesNotRetryEveryTime()
    {
        var source = new FakeSource { Respond = _ => Task.FromResult(ReleaseFetchResult.Fail(UpdateFailure.Offline)) };
        var clock = new Clock(); var checker = new UpdateChecker(source, V("0.2.0"), clock.Read);
        Assert.Equal(UpdateFailure.Offline, (await checker.CheckAsync(false)).Failure);
        clock.Now += TimeSpan.FromMinutes(5);
        await checker.CheckAsync(false); Assert.Equal(1, source.Calls);
        clock.Now += TimeSpan.FromMinutes(6);
        source.Respond = _ => Task.FromResult(Releases(("v0.2.1", false, false)));
        Assert.Equal(UpdateState.UpdateAvailable, (await checker.CheckAsync(false)).State); Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task ManualRefreshBypassesTheCache()
    {
        var source = new FakeSource(); var clock = new Clock();
        var checker = new UpdateChecker(source, V("0.2.0"), clock.Read);
        await checker.CheckAsync(false);
        clock.Now += TimeSpan.FromSeconds(10);
        await checker.CheckAsync(true);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task RapidManualClicksReuseTheRunningRequest()
    {
        var gate = new TaskCompletionSource<ReleaseFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource { Respond = _ => gate.Task }; var clock = new Clock();
        var checker = new UpdateChecker(source, V("0.2.0"), clock.Read);
        var first = checker.CheckAsync(true);
        var tasks = Enumerable.Range(0, 20).Select(_ => checker.CheckAsync(true)).Prepend(first).ToArray();
        Assert.Equal(1, source.Calls);
        gate.SetResult(Releases(("v0.2.1", false, false)));
        Assert.All(await Task.WhenAll(tasks), status => Assert.Equal(UpdateState.UpdateAvailable, status.State));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task ManualRefreshAfterTheSpacingWindowSupersedesAnObsoleteRequestWhoseLateResultIsIgnored()
    {
        var first = new TaskCompletionSource<ReleaseFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ReleaseFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = new Queue<TaskCompletionSource<ReleaseFetchResult>>([first, second]);
        var source = new FakeSource { Respond = _ => responses.Dequeue().Task }; var clock = new Clock();
        var checker = new UpdateChecker(source, V("0.2.0"), clock.Read);
        var published = new List<UpdateStatus>(); checker.StatusChanged += published.Add;

        var oldRequest = checker.CheckAsync(false);
        clock.Now += TimeSpan.FromSeconds(10);
        var newRequest = checker.CheckAsync(true);
        Assert.Equal(2, source.Calls);

        second.SetResult(Releases(("v0.2.1", false, false)));
        Assert.Equal(UpdateState.UpdateAvailable, (await newRequest).State);
        first.SetResult(Releases(("v0.2.0", false, false)));   // obsolete answer arrives late
        await oldRequest;

        Assert.Equal(UpdateState.UpdateAvailable, checker.Cached!.State);
        Assert.Equal([UpdateState.Checking, UpdateState.Checking, UpdateState.UpdateAvailable], published.Select(status => status.State));
    }

    [Fact]
    public async Task CheckingIsAnnouncedBeforeAnInstantAnswerAndTheFinalStatusIsLast()
    {
        var source = new FakeSource(); var checker = new UpdateChecker(source, V("0.2.0"));
        var published = new List<UpdateState>(); checker.StatusChanged += status => published.Add(status.State);
        await checker.CheckAsync(true);
        Assert.Equal([UpdateState.Checking, UpdateState.UpdateAvailable], published);
    }

    [Fact]
    public async Task SourceExceptionBecomesUnavailableAndDoesNotPoisonLaterChecks()
    {
        var source = new FakeSource { Respond = _ => throw new InvalidOperationException("boom") };
        var clock = new Clock(); var checker = new UpdateChecker(source, V("0.2.0"), clock.Read);
        Assert.Equal(UpdateState.Unavailable, (await checker.CheckAsync(false)).State);
        clock.Now += TimeSpan.FromSeconds(10);
        source.Respond = _ => Task.FromResult(Releases(("v0.2.0", false, false)));
        Assert.Equal(UpdateState.UpToDate, (await checker.CheckAsync(true)).State);
    }

    // ---- Settings view-model states.
    [Fact]
    public void ViewModelShowsEachUpdateStateWithoutAFatalLook()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        viewModel.UpdateUpdateStatus(UpdateStatus.Checking);
        Assert.Equal("Checking for updates...", viewModel.UpdateStatusText); Assert.False(viewModel.CanCheckForUpdates); Assert.False(viewModel.CanViewRelease);

        viewModel.UpdateUpdateStatus(Evaluate("0.2.0", ("v0.2.0", false, false)));
        Assert.Equal("You're up to date.", viewModel.UpdateStatusText); Assert.Equal("Latest version: v0.2.0", viewModel.UpdateDetailText);
        Assert.True(viewModel.CanCheckForUpdates); Assert.False(viewModel.CanViewRelease);

        viewModel.UpdateUpdateStatus(Evaluate("0.3.0", ("v0.2.0", false, false)));
        Assert.Equal("You're up to date.", viewModel.UpdateStatusText); Assert.Contains("newer than the latest published release (v0.2.0)", viewModel.UpdateDetailText);

        viewModel.UpdateUpdateStatus(Evaluate("0.2.0", ("v0.2.1", false, false)));
        Assert.Equal("A new version is available: v0.2.1", viewModel.UpdateStatusText); Assert.True(viewModel.CanViewRelease);

        viewModel.UpdateUpdateStatus(new UpdateStatus(UpdateState.Unavailable, Failure: UpdateFailure.Offline));
        Assert.Equal("Could not check for updates.", viewModel.UpdateStatusText); Assert.Equal("GitHub could not be reached.", viewModel.UpdateDetailText);
        Assert.True(viewModel.CanCheckForUpdates); Assert.False(viewModel.CanViewRelease);
        foreach (var failure in Enum.GetValues<UpdateFailure>().Where(failure => failure != UpdateFailure.None))
        {
            viewModel.UpdateUpdateStatus(new UpdateStatus(UpdateState.Unavailable, Failure: failure, StatusCode: 500));
            Assert.False(string.IsNullOrWhiteSpace(viewModel.UpdateDetailText));
            Assert.DoesNotContain("Exception", viewModel.UpdateDetailText);
        }
    }

    [Fact]
    public void ViewModelRequestsOnlyKnownLinksAndOnlyChecksWhenIdle()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var links = new List<string>(); var checks = 0;
        viewModel.LinkOpenRequested += links.Add; viewModel.UpdateCheckRequested += () => checks++;

        viewModel.UpdateUpdateStatus(UpdateStatus.Checking);
        viewModel.RequestUpdateCheck(); viewModel.RequestUpdateCheck();
        Assert.Equal(0, checks);                                   // button disabled while a request runs
        viewModel.RequestOpenLatestRelease(); Assert.Empty(links); // nothing to open yet

        viewModel.UpdateUpdateStatus(Evaluate("0.2.0", ("v0.2.1", false, false)));
        viewModel.RequestUpdateCheck(); Assert.Equal(1, checks);
        viewModel.RequestOpenLatestRelease(); viewModel.RequestOpenRepository(); viewModel.RequestOpenReleases();
        Assert.Equal(["https://github.com/jimmy8790/DevOverlay/releases/tag/v0.2.1", AppLinks.RepositoryUrl, AppLinks.ReleasesUrl], links);
        Assert.All(links, link => Assert.True(AppLinks.IsAllowedBrowserUrl(link)));

        viewModel.UpdateUpdateStatus(Evaluate("0.2.1", ("v0.2.1", false, false)));
        links.Clear(); viewModel.RequestOpenLatestRelease(); Assert.Empty(links); // up to date: no stale release link
    }
}
