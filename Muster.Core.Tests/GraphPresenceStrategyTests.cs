using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Muster.Core.Config;
using Muster.Core.Presence;
using Muster.Graph;

namespace Muster.Core.Tests;

/// <summary>
/// The Graph client, against a stubbed handler. Never against a live tenant: a test that can
/// change someone's real status is not a test.
/// </summary>
public sealed class GraphPresenceStrategyTests
{
    private static readonly PresenceAccount Account = new(
        "teams-fabrikam",
        "fabrikam",
        "Fabrikam",
        PresenceStrategyKind.Graph,
        "james@fabrikam.example",
        "11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Setting_busy_posts_the_preferred_presence_with_an_expiry()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        var applied = await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None);

        Assert.True(applied);

        var post = handler.Calls.Last();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/presence/setUserPreferredPresence", post.Uri);

        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal("Busy", body.RootElement.GetProperty("availability").GetString());

        // InACall is a reported activity, not a settable preferred one, so Busy/Busy is what works.
        Assert.Equal("Busy", body.RootElement.GetProperty("activity").GetString());
        Assert.Equal("PT2H", body.RootElement.GetProperty("expirationDuration").GetString());
    }

    [Fact]
    public async Task The_request_carries_the_account_token_as_a_bearer()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None);

        Assert.All(handler.Calls, call => Assert.Equal("Bearer token-for-james@fabrikam.example", call.Authorization));
    }

    [Fact]
    public async Task Presence_is_read_before_it_is_written()
    {
        // SPEC 8.3: preferred presence is a no-op without an active presence session, and that is
        // to be checked and logged rather than assumed.
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/presence", handler.Calls[0].Uri);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Method);
    }

    [Fact]
    public async Task A_signed_out_tab_is_logged_but_does_not_stop_the_write()
    {
        // The preference sticks and applies as soon as a session appears, so refusing to write it
        // would only mean forgetting about a call that is still going on.
        var handler = new StubHandler { Availability = "Offline" };
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_failed_presence_read_does_not_stop_the_write()
    {
        var handler = new StubHandler { PresenceReadStatus = HttpStatusCode.ServiceUnavailable };
        var strategy = Strategy(handler);

        Assert.True(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_posts_to_the_clear_endpoint_with_no_body()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        var cleared = await strategy.ClearAsync(Account, CancellationToken.None);

        Assert.True(cleared);

        var post = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/presence/clearUserPreferredPresence", post.Uri);
        Assert.Null(post.Body);
    }

    [Fact]
    public async Task A_missing_expiry_is_refused_without_calling_graph()
    {
        // Preferred presence never auto-reverts. A set with no expiry is the one that leaves
        // someone Busy indefinitely, so it is refused rather than guessed at.
        var handler = new StubHandler();
        var strategy = Strategy(handler);

        Assert.False(await strategy.SetBusyAsync(Account, "  ", CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task An_account_that_has_never_signed_in_writes_nothing()
    {
        var handler = new StubHandler();
        var strategy = Strategy(handler, new StubTokens { Token = null });

        Assert.False(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
        Assert.False(await strategy.ClearAsync(Account, CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Graph_refusing_is_reported_as_failure_not_thrown(HttpStatusCode status)
    {
        var handler = new StubHandler { WriteStatus = status };
        var strategy = Strategy(handler);

        Assert.False(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
        Assert.False(await strategy.ClearAsync(Account, CancellationToken.None));
    }

    [Fact]
    public async Task A_transport_failure_is_reported_as_failure_not_thrown()
    {
        var handler = new StubHandler { Throw = true };
        var strategy = Strategy(handler);

        Assert.False(await strategy.SetBusyAsync(Account, "PT2H", CancellationToken.None));
    }

    private static GraphPresenceStrategy Strategy(StubHandler handler, StubTokens? tokens = null)
        => new(
            tokens ?? new StubTokens(),
            new HttpClient(handler) { BaseAddress = GraphPresenceStrategy.DefaultBaseAddress },
            NullLogger<GraphPresenceStrategy>.Instance);

    private sealed record Call(HttpMethod Method, string Uri, string? Authorization, string? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        public string Availability { get; set; } = "Available";

        public HttpStatusCode PresenceReadStatus { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode WriteStatus { get; set; } = HttpStatusCode.OK;

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
                request.Headers.Authorization?.ToString(),
                body));

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(PresenceReadStatus)
                {
                    Content = new StringContent(
                        $$"""{"availability":"{{Availability}}","activity":"{{Availability}}"}"""),
                };
            }

            return new HttpResponseMessage(WriteStatus)
            {
                Content = new StringContent(WriteStatus == HttpStatusCode.OK ? string.Empty : "{\"error\":{}}"),
            };
        }
    }

    private sealed class StubTokens : IGraphTokenProvider
    {
        public string? Token { get; init; } = "unset";

        public Task<string?> GetTokenSilentAsync(PresenceAccount account, CancellationToken ct = default)
            => Task.FromResult(Token == "unset" ? $"token-for-{account.UserPrincipalName}" : Token);

        public Task<GraphSignInResult> SignInAsync(PresenceAccount account, CancellationToken ct = default)
            => Task.FromResult(GraphSignInResult.Ok(account.UserPrincipalName));

        public Task<bool> IsSignedInAsync(PresenceAccount account, CancellationToken ct = default)
            => Task.FromResult(Token is not null);

        public Task SignOutAsync(PresenceAccount account, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
