using Microsoft.Extensions.Time.Testing;
using Muster.Core.Config;
using Muster.Core.Hosting;

namespace Muster.Core.Tests;

/// <summary>
/// Suspension decides which background pages stop running. Every case here is one where getting
/// it wrong means a service silently stops delivering notifications, which is the one failure this
/// app cannot afford: the user finds out hours later, from someone asking why they did not reply.
/// </summary>
public sealed class SuspensionCoordinatorTests
{
    private static readonly SuspensionConfig Config = new() { Enabled = true, IdleMinutes = 5 };

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-08-31T09:00:00Z"));

    // ---- what is never suspended -------------------------------------------------------------

    [Fact]
    public void KeepAlive_sessions_are_never_suspended()
    {
        // The whole point of keepAlive is a service that runs whether or not it is on screen.
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("teams-fabrikam", "fabrikam", keepAlive: true));

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(suspended);
    }

    [Fact]
    public void Nothing_in_the_active_workspace_is_suspended()
    {
        // SPEC 6.1 is explicit: a background tab beside the one being read keeps running.
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-datebyte", "datebyte"));

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(suspended);
    }

    [Fact]
    public void A_session_holding_audio_is_not_suspended()
    {
        // Belt and braces over WebView2's own refusal. Suspending a page mid-call would drop it.
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("meet-fabrikam", "fabrikam"));
        coordinator.SetCallSessions(new HashSet<string> { "meet-fabrikam" });

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(suspended);
    }

    [Fact]
    public void A_session_is_suspendable_again_once_its_call_ends()
    {
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("meet-fabrikam", "fabrikam"));
        coordinator.SetCallSessions(new HashSet<string> { "meet-fabrikam" });
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(suspended);

        coordinator.SetCallSessions(new HashSet<string>());
        _clock.Advance(SuspensionCoordinator.PollInterval);

        Assert.Contains("meet-fabrikam", suspended);
    }

    // ---- the idle window ---------------------------------------------------------------------

    [Fact]
    public void A_background_workspace_is_suspended_once_it_has_been_idle_long_enough()
    {
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));

        _clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(["azure-fabrikam"], suspended.Distinct());
    }

    [Fact]
    public void A_background_workspace_is_left_alone_inside_the_idle_window()
    {
        // Switching away to answer one message and switching straight back must not cost a page
        // reload, which is what suspending and resuming inside a few seconds would look like.
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));

        _clock.Advance(TimeSpan.FromMinutes(4));

        Assert.Empty(suspended);
    }

    [Fact]
    public void Leaving_a_workspace_starts_its_clock_from_the_moment_it_left()
    {
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-datebyte", "datebyte"));

        // An hour on screen must not count as an hour idle.
        _clock.Advance(TimeSpan.FromHours(1));
        coordinator.SetActiveWorkspace("fabrikam");
        _clock.Advance(TimeSpan.FromMinutes(4));

        Assert.Empty(suspended);

        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Contains("azure-datebyte", suspended);
    }

    [Fact]
    public void Passing_through_a_workspace_does_not_reset_the_others()
    {
        // Flicking down the rail touches every workspace in turn. If each switch restarted every
        // other workspace's clock, nothing would ever reach the threshold.
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("a");
        coordinator.Track(Session("svc-a", "a"));
        coordinator.Track(Session("svc-b", "b"));
        coordinator.Track(Session("svc-c", "c"));

        coordinator.SetActiveWorkspace("b");
        _clock.Advance(TimeSpan.FromMinutes(3));
        coordinator.SetActiveWorkspace("c");
        _clock.Advance(TimeSpan.FromMinutes(3));

        // 'a' has been off screen for six minutes, 'b' for three.
        Assert.Contains("svc-a", suspended);
        Assert.DoesNotContain("svc-b", suspended);
    }

    [Fact]
    public void A_session_started_into_a_background_workspace_starts_counting_immediately()
    {
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        _clock.Advance(TimeSpan.FromHours(2));
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(suspended);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains("azure-fabrikam", suspended);
    }

    // ---- waking up ---------------------------------------------------------------------------

    [Fact]
    public void Switching_to_a_workspace_wakes_every_session_in_it_at_once()
    {
        // SPEC 6.1 resumes on the switch. Waiting for the next tick would show a dead page.
        using var coordinator = Create(out _, out var resumed);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("teams-fabrikam", "fabrikam", keepAlive: true));
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        coordinator.Track(Session("azure-datebyte", "datebyte"));
        resumed.Clear();

        coordinator.SetActiveWorkspace("fabrikam");

        Assert.Equal(["teams-fabrikam", "azure-fabrikam"], resumed);
    }

    [Fact]
    public void Re_selecting_the_workspace_already_shown_changes_nothing()
    {
        using var coordinator = Create(out var suspended, out var resumed);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        _clock.Advance(TimeSpan.FromMinutes(6));
        suspended.Clear();
        resumed.Clear();

        coordinator.SetActiveWorkspace("datebyte");

        Assert.Empty(resumed);
    }

    // ---- the off switch ----------------------------------------------------------------------

    [Fact]
    public void Suspension_turned_off_suspends_nothing()
    {
        using var coordinator = Create(out var suspended, out _, new SuspensionConfig { Enabled = false });

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));

        _clock.Advance(TimeSpan.FromHours(4));

        Assert.Empty(suspended);
    }

    [Fact]
    public void Turning_suspension_off_wakes_everything_immediately()
    {
        // The setting is the escape hatch for a service that misbehaves asleep. An escape hatch
        // that waits for you to visit each workspace in turn is not one.
        using var coordinator = Create(out _, out var resumed);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        coordinator.Track(Session("azure-brand", "brand"));
        _clock.Advance(TimeSpan.FromMinutes(6));
        resumed.Clear();

        coordinator.Apply(new SuspensionConfig { Enabled = false });

        Assert.Equal(["azure-fabrikam", "azure-brand"], resumed);
    }

    [Fact]
    public void Turning_suspension_off_twice_does_not_wake_everything_twice()
    {
        using var coordinator = Create(out _, out var resumed, new SuspensionConfig { Enabled = false });

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        resumed.Clear();

        coordinator.Apply(new SuspensionConfig { Enabled = false });

        Assert.Empty(resumed);
    }

    [Fact]
    public void A_shortened_idle_window_applies_to_sessions_already_counting()
    {
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(suspended);

        coordinator.Apply(Config with { IdleMinutes = 1 });
        _clock.Advance(SuspensionCoordinator.PollInterval);

        Assert.Contains("azure-fabrikam", suspended);
    }

    // ---- teardown ----------------------------------------------------------------------------

    [Fact]
    public void A_forgotten_session_is_never_mentioned_again()
    {
        // An ephemeral tab that closes mid-countdown must not have its id announced afterwards;
        // the host would be asked to suspend a control it has already disposed.
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("eph-1", "fabrikam"));
        coordinator.Forget("eph-1");

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(suspended);
    }

    [Fact]
    public void Re_tracking_a_session_takes_the_new_keepAlive_without_restarting_its_clock()
    {
        // keepAlive is editable without recreating the session, so a reload re-tracks. If that
        // reset the clock, a config saved every few minutes would keep everything awake forever.
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam", keepAlive: true));
        _clock.Advance(TimeSpan.FromMinutes(4));

        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Contains("azure-fabrikam", suspended);
    }

    [Fact]
    public void Re_tracking_a_session_as_keepAlive_stops_it_being_suspended()
    {
        using var coordinator = Create(out var suspended, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        _clock.Advance(TimeSpan.FromMinutes(4));

        coordinator.Track(Session("azure-fabrikam", "fabrikam", keepAlive: true));
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(suspended);
    }

    [Fact]
    public void Suspendable_count_reports_what_should_be_asleep()
    {
        using var coordinator = Create(out _, out _);

        coordinator.SetActiveWorkspace("datebyte");
        coordinator.Track(Session("teams-fabrikam", "fabrikam", keepAlive: true));
        coordinator.Track(Session("azure-fabrikam", "fabrikam"));
        coordinator.Track(Session("azure-datebyte", "datebyte"));

        _clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, coordinator.SuspendableCount);
        Assert.True(coordinator.IsSuspendable("azure-fabrikam"));
        Assert.False(coordinator.IsSuspendable("teams-fabrikam"));
        Assert.False(coordinator.IsSuspendable("azure-datebyte"));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private SuspensionCoordinator Create(
        out List<string> suspended,
        out List<string> resumed,
        SuspensionConfig? config = null)
    {
        var sleep = new List<string>();
        var wake = new List<string>();
        var coordinator = new SuspensionCoordinator(config ?? Config, _clock);

        coordinator.SuspendRequested += (_, descriptor) => sleep.Add(descriptor.Id);
        coordinator.ResumeRequested += (_, descriptor) => wake.Add(descriptor.Id);

        suspended = sleep;
        resumed = wake;
        return coordinator;
    }

    private static SessionDescriptor Session(string id, string workspaceId, bool keepAlive = false)
        => new(id, workspaceId, id, $"ws-{workspaceId}", new Uri("https://example.invalid/"))
        {
            KeepAlive = keepAlive,
        };
}
