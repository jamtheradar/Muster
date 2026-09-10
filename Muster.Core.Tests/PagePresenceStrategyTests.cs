using System.Net;
using Muster.Core.Config;
using Muster.Core.Presence;

namespace Muster.Core.Tests;

/// <summary>
/// The page strategy, against a stubbed handler. Never against a live tenant: a test that can
/// change someone's real status is not a test.
/// </summary>
/// <remarks>
/// The contract asserted here was captured by watching Teams do it, not read from documentation,
/// which is exactly why it is pinned down this precisely. If Microsoft changes it, these tests are
/// the record of what it used to be.
/// </remarks>
public sealed class PagePresenceStrategyTests
{
    private const string Mri = "8:orgid:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static readonly PresenceAccount Account = new(
        "teams-fabrikam",
        "fabrikam",
        "Fabrikam",
        PresenceStrategyKind.Page,
        UserPrincipalName: string.Empty,
        TenantId: string.Empty);

    [Fact]
    public async Task Setting_busy_puts_the_availability_to_the_regional_endpoint()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));

        var write = handler.Calls[0];
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.Equal("https://teams.cloud.microsoft/ups/apac/v1/me/forceavailability/", write.Uri);
        Assert.Equal("""{"availability":"Busy"}""", write.Body);
    }

    [Fact]
    public async Task Releasing_puts_an_empty_body_to_the_same_place()
    {
        // The empty body is the whole trick and it is not guessable. Sending
        // {"availability":"Available"} instead would pin Available rather than release, so you
        // would show available during a real meeting in that tenant.
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        Assert.True(await strategy.ClearAsync(Account, CancellationToken.None));

        var write = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.Equal("https://teams.cloud.microsoft/ups/apac/v1/me/forceavailability/", write.Uri);
        Assert.Equal(string.Empty, write.Body);
    }

    [Fact]
    public async Task The_captured_credentials_are_replayed_verbatim()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None);

        Assert.All(handler.Calls, call =>
        {
            Assert.Equal("Bearer captured-token", call.Authorization);
            Assert.Equal("authtoken=captured", call.Cookie);
        });
    }

    [Fact]
    public async Task A_set_is_read_back_and_confirmed()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));

        var read = handler.Calls[1];
        Assert.Equal(HttpMethod.Post, read.Method);
        Assert.Equal("https://teams.cloud.microsoft/ups/apac/v1/presence/getpresence/", read.Uri);
        Assert.Contains(Mri, read.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_accepted_but_ignored_is_reported_as_failure()
    {
        // The failure this whole read-back exists for. A 2xx means the request was accepted, not
        // that anything changed, and believing otherwise means walking around thinking you show
        // Busy. It is also how we would find out the undocumented route had moved.
        var handler = new StubHandler { Reports = "Available" };
        var strategy = Strategy(handler);

        Assert.False(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
    }

    [Theory]
    [InlineData("BusyIdle")]
    [InlineData("InAMeeting")]
    [InlineData("InACall")]
    [InlineData("DoNotDisturb")]
    public async Task Presence_that_merely_means_busy_counts_as_confirmation(string reported)
    {
        // What comes back is effective presence, not what we wrote. An idle machine reports
        // BusyIdle and a real meeting reports InAMeeting; both mean the force landed, and calling
        // either a failure would report a working write as broken.
        var handler = new StubHandler { Reports = reported };
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
    }

    [Fact]
    public async Task Confirmation_waits_for_the_change_to_propagate()
    {
        // The first version of this read back 125ms after the write and called a stale value a
        // failure. Effective presence is recomputed asynchronously, so an immediate read is
        // reading the past.
        var handler = new StubHandler { Reports = "Away", BecomesBusyAfterReads = 2 };
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
        Assert.True(handler.Calls.Count(call => call.Method == HttpMethod.Post) >= 3);
    }

    [Fact]
    public async Task Read_back_matches_on_identity_rather_than_position()
    {
        // getpresence also serves colleagues. Reading the first entry would give a confident,
        // wrong answer the moment Teams returns them in another order.
        var handler = new StubHandler { IncludeStranger = true };
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_known_identity_the_write_still_happens_but_is_not_confirmed()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler, mri: null);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));

        // The write, and no read: there is nothing to ask about.
        var write = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, write.Method);
    }

    [Fact]
    public async Task A_session_that_has_not_been_seen_writes_nothing()
    {
        var handler = new StubHandler();
        var strategy = new PagePresenceStrategy(
            new StubSessions(null),
            new HttpClient(handler),
            IPresenceStrategyLog.Silent);

        Assert.False(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
        Assert.False(await strategy.ClearAsync(Account, CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Teams_refusing_the_write_is_a_failure_not_an_exception(HttpStatusCode status)
    {
        var handler = new StubHandler { WriteStatus = status };
        var strategy = Strategy(handler);

        Assert.False(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
        Assert.False(await strategy.ClearAsync(Account, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_read_back_does_not_condemn_a_write_that_was_accepted()
    {
        // Losing the network between the write and the check says nothing about the write.
        var handler = new StubHandler { ReadStatus = HttpStatusCode.ServiceUnavailable };
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
    }

    [Fact]
    public async Task A_transport_failure_is_a_failure_not_an_exception()
    {
        var handler = new StubHandler { Throw = true };
        var strategy = Strategy(handler);

        Assert.False(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
    }

    [Fact]
    public void It_answers_to_the_page_strategy_and_nothing_else()
        => Assert.Equal(PresenceStrategyKind.Page, Strategy(new StubHandler()).Kind);

    private static PagePresenceStrategy Strategy(StubHandler handler, string? mri = Mri)
        => new(
            new StubSessions(new TeamsPresenceSession(
                new Uri("https://teams.cloud.microsoft/ups/apac/v1/"),
                "Bearer captured-token",
                "authtoken=captured",
                mri)),
            new HttpClient(handler),
            IPresenceStrategyLog.Silent,

            // The real window is six seconds of patience for a status that propagates
            // asynchronously. Tests assert the logic, not the waiting.
            confirmationWindow: TimeSpan.FromMilliseconds(120),
            confirmationInterval: TimeSpan.FromMilliseconds(20));

    private sealed record Call(HttpMethod Method, string Uri, string? Authorization, string? Cookie, string? Body);

    private sealed class StubSessions(TeamsPresenceSession? session) : ITeamsPresenceSessionSource
    {
        public TeamsPresenceSession? For(string sessionId) => session;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        public string Reports { get; set; } = "Busy";

        /// <summary>Reports Busy only after this many reads, standing in for propagation lag.</summary>
        public int BecomesBusyAfterReads { get; set; }

        public bool IncludeStranger { get; set; }

        public HttpStatusCode WriteStatus { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode ReadStatus { get; set; } = HttpStatusCode.OK;

        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new HttpRequestException("no network");
            }

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Calls.Add(new Call(
                request.Method,
                request.RequestUri!.ToString(),
                Header(request, "authorization"),
                Header(request, "Cookie"),
                body));

            if (request.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(WriteStatus);
            }

            var reads = Calls.Count(call => call.Method == HttpMethod.Post);
            var settled = BecomesBusyAfterReads > 0 && reads > BecomesBusyAfterReads;
            var mine = Entry(Mri, settled ? "Busy" : Reports);
            var stranger = Entry("8:orgid:99999999-9999-9999-9999-999999999999", "Away");

            return new HttpResponseMessage(ReadStatus)
            {
                Content = new StringContent(IncludeStranger ? $"[{stranger},{mine}]" : $"[{mine}]"),
            };
        }

        /// <summary>One entry of a getpresence response, in the shape the real one comes back in.</summary>
        private static string Entry(string mri, string availability) => System.Text.Json.JsonSerializer.Serialize(
            new { mri, presence = new { availability, activity = availability } });

        private static string? Header(HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;
    }
}
