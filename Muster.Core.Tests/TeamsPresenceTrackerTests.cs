using Muster.Core.Presence;

namespace Muster.Core.Tests;

/// <summary>
/// The tracker decides two things that are easy to get wrong and expensive to get wrong quietly:
/// which URL gives the regional API base, and which observed identity is actually yours.
/// </summary>
public sealed class TeamsPresenceTrackerTests
{
    private const string Session = "teams-fabrikam";
    private const string Mine = "8:orgid:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Theirs = "8:orgid:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Theory]
    [InlineData("https://teams.cloud.microsoft/ups/apac/v1/me/forceavailability/", "https://teams.cloud.microsoft/ups/apac/v1/")]
    [InlineData("https://teams.cloud.microsoft/ups/emea/v1/presence/getpresence/", "https://teams.cloud.microsoft/ups/emea/v1/")]
    [InlineData("https://teams.cloud.microsoft/ups/noam/v2/me/endpoints/", "https://teams.cloud.microsoft/ups/noam/v2/")]
    public void The_api_base_carries_the_region_and_version_it_was_seen_with(string uri, string expected)
    {
        // Never constructed. A hardcoded apac would fail for a client homed elsewhere, silently,
        // which is the worst way for presence in particular to break.
        Assert.Equal(new Uri(expected), TeamsPresenceTracker.BaseOf(new Uri(uri)));
    }

    [Theory]
    [InlineData("https://teams.cloud.microsoft/api/csa/v1/thing")]
    [InlineData("https://teams.cloud.microsoft/ups/")]
    [InlineData("https://example.com/whatever")]
    public void A_url_that_is_not_a_presence_call_yields_no_base(string uri)
        => Assert.Null(TeamsPresenceTracker.BaseOf(new Uri(uri)));

    [Fact]
    public void A_single_subject_getpresence_reveals_this_tabs_own_identity()
    {
        var mri = TeamsPresenceTracker.OwnMriFrom(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/presence/getpresence/",
            body: $$"""[{"mri":"{{Mine}}","source":"ups"}]"""));

        Assert.Equal(Mine, mri);
    }

    [Fact]
    public void A_batch_getpresence_reveals_nothing()
    {
        // The same endpoint fetches colleagues' presence. Adopting one of those as your own
        // identity would mean checking somebody else's status to decide whether your write worked.
        var mri = TeamsPresenceTracker.OwnMriFrom(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/presence/getpresence/",
            body: $$"""[{"mri":"{{Mine}}","source":"ups"},{"mri":"{{Theirs}}","source":"ups"}]"""));

        Assert.Null(mri);
    }

    [Fact]
    public void A_subscription_body_is_not_an_identity()
    {
        // It is full of colleagues' MRIs and is not about you at all.
        var mri = TeamsPresenceTracker.OwnMriFrom(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/pubsub/subscriptions/abc",
            body: $$"""{"subscriptionsToAdd":[{"mri":"{{Theirs}}","source":"ups"}]}"""));

        Assert.Null(mri);
    }

    [Fact]
    public void A_malformed_body_is_ignored_rather_than_thrown_over()
    {
        var mri = TeamsPresenceTracker.OwnMriFrom(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/presence/getpresence/",
            body: "{ not json"));

        Assert.Null(mri);
    }

    [Fact]
    public void Observing_a_request_makes_a_session_writable()
    {
        var tracker = new TeamsPresenceTracker();

        Assert.True(tracker.Observe(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/me/endpoints/",
            authorization: "Bearer abc")));

        var session = tracker.For(Session);

        Assert.NotNull(session);
        Assert.True(session.IsUsable);
        Assert.Equal(new Uri("https://teams.cloud.microsoft/ups/apac/v1/"), session.Endpoint);

        // No identity seen yet, so a write can happen but cannot be confirmed.
        Assert.False(session.CanVerify);
    }

    [Fact]
    public void The_first_identity_seen_is_the_one_kept()
    {
        // Teams asks for its own presence before anyone else's, and a later single-subject call
        // could legitimately be about a colleague whose card you opened.
        var tracker = new TeamsPresenceTracker();

        tracker.Observe(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/presence/getpresence/",
            authorization: "Bearer abc",
            body: $$"""[{"mri":"{{Mine}}","source":"ups"}]"""));

        tracker.Observe(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/presence/getpresence/",
            authorization: "Bearer abc",
            body: $$"""[{"mri":"{{Theirs}}","source":"ups"}]"""));

        Assert.Equal(Mine, tracker.For(Session)?.Mri);
    }

    [Fact]
    public void A_later_request_without_a_token_does_not_erase_the_one_we_have()
    {
        var tracker = new TeamsPresenceTracker();

        tracker.Observe(Observation(
            "https://teams.cloud.microsoft/ups/apac/v1/me/endpoints/",
            authorization: "Bearer abc"));

        tracker.Observe(Observation("https://teams.cloud.microsoft/ups/apac/v1/me/endpoints/"));

        Assert.True(tracker.For(Session)?.IsUsable);
    }

    [Fact]
    public void A_refreshed_token_replaces_the_old_one()
    {
        var tracker = new TeamsPresenceTracker();

        tracker.Observe(Observation("https://teams.cloud.microsoft/ups/apac/v1/me/endpoints/", authorization: "Bearer old"));
        tracker.Observe(Observation("https://teams.cloud.microsoft/ups/apac/v1/me/endpoints/", authorization: "Bearer new"));

        Assert.Equal("Bearer new", tracker.For(Session)?.Authorization);
    }

    [Fact]
    public void Sessions_are_kept_apart()
    {
        var tracker = new TeamsPresenceTracker();

        tracker.Observe(Observation("https://teams.cloud.microsoft/ups/apac/v1/me/endpoints/", authorization: "Bearer a"));
        tracker.Observe(Observation(
            "https://teams.cloud.microsoft/ups/emea/v1/me/endpoints/",
            authorization: "Bearer b",
            session: "teams-datebyte"));

        Assert.Equal("Bearer a", tracker.For(Session)?.Authorization);
        Assert.Equal("Bearer b", tracker.For("teams-datebyte")?.Authorization);
        Assert.Equal(new Uri("https://teams.cloud.microsoft/ups/emea/v1/"), tracker.For("teams-datebyte")?.Endpoint);
    }

    [Fact]
    public void A_forgotten_session_takes_its_token_with_it()
    {
        var tracker = new TeamsPresenceTracker();

        tracker.Observe(Observation("https://teams.cloud.microsoft/ups/apac/v1/me/endpoints/", authorization: "Bearer abc"));
        tracker.Forget(Session);

        Assert.Null(tracker.For(Session));
        Assert.Empty(tracker.KnownSessions);
    }

    [Fact]
    public void An_unrelated_request_teaches_it_nothing()
    {
        var tracker = new TeamsPresenceTracker();

        Assert.False(tracker.Observe(Observation("https://teams.cloud.microsoft/api/chat/v1/messages", authorization: "Bearer abc")));
        Assert.Null(tracker.For(Session));
    }

    [Fact]
    public void The_token_is_never_in_the_string_representation()
    {
        // This record ends up in log messages and exception text by accident sooner or later.
        var session = new TeamsPresenceSession(
            new Uri("https://teams.cloud.microsoft/ups/apac/v1/"),
            "Bearer super-secret-value",
            "authtoken=also-secret",
            Mine);

        var text = session.ToString();

        Assert.DoesNotContain("super-secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", text, StringComparison.Ordinal);
        Assert.Contains(Mine, text, StringComparison.Ordinal);
    }

    private static TeamsPresenceObservation Observation(
        string uri,
        string? authorization = null,
        string? body = null,
        string session = Session)
        => new(session, new Uri(uri), authorization, "cookie=value", body);
}
