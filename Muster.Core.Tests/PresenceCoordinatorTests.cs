using Microsoft.Extensions.Time.Testing;
using Muster.Core.Config;
using Muster.Core.Presence;

namespace Muster.Core.Tests;

/// <summary>
/// The presence state machine decides whether real people in real tenants see you as Busy, so it
/// gets the closest scrutiny in the codebase. Cases here are the ones CLAUDE.md mandates.
/// </summary>
public sealed class PresenceCoordinatorTests
{
    private static readonly PresenceConfig Config = new()
    {
        CallDetectDelaySeconds = 3,
        CallClearDelaySeconds = 10,
    };

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-08-29T09:00:00Z"));

    // ---- debounce on acquire ---------------------------------------------------------------

    [Fact]
    public void Holding_audio_briefly_is_not_a_call()
    {
        // A device test dialog opens the microphone for a moment. That must not move anyone's
        // status, which is the whole reason for the detect delay.
        using var presence = Create();

        presence.ReportAudioAcquired("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(2));
        presence.ReportAudioReleased("teams-a");
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(CallState.Idle, presence.State);
    }

    [Fact]
    public void Audio_is_not_a_call_until_the_detect_delay_has_passed()
    {
        using var presence = Create();

        presence.ReportAudioAcquired("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(CallState.Idle, presence.State);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CallState.InCall, presence.State);
        Assert.Equal("teams-a", presence.SourceSessionId);
    }

    // ---- debounce on release ---------------------------------------------------------------

    [Fact]
    public void Releasing_audio_does_not_clear_until_the_quiet_window_passes()
    {
        using var presence = InCall("teams-a");

        presence.ReportAudioReleased("teams-a");
        Assert.Equal(CallState.Clearing, presence.State);

        _clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(CallState.Clearing, presence.State);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CallState.Idle, presence.State);
    }

    [Fact]
    public void Audio_returning_during_the_quiet_window_stays_in_the_same_call()
    {
        // Switching headsets mid-call drops and reacquires the microphone. That must not clear
        // presence and then set it again.
        using var presence = InCall("teams-a");

        presence.ReportAudioReleased("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(4));
        presence.ReportAudioAcquired("teams-a");

        Assert.Equal(CallState.InCall, presence.State);
        Assert.Equal("teams-a", presence.SourceSessionId);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CallState.InCall, presence.State);
    }

    // ---- overlapping calls in two sessions --------------------------------------------------

    [Fact]
    public void A_second_session_joining_keeps_the_original_source()
    {
        using var presence = InCall("teams-a");

        presence.ReportAudioAcquired("teams-b");

        Assert.Equal(CallState.InCall, presence.State);
        Assert.Equal("teams-a", presence.SourceSessionId);
        Assert.Contains("teams-b", presence.CallSessions);
    }

    [Fact]
    public void The_call_lasts_until_every_session_is_quiet()
    {
        using var presence = InCall("teams-a");
        presence.ReportAudioAcquired("teams-b");

        presence.ReportAudioReleased("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(30));

        // teams-b is still holding audio, so this is still a call.
        Assert.Equal(CallState.InCall, presence.State);

        presence.ReportAudioReleased("teams-b");
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CallState.Idle, presence.State);
    }

    [Fact]
    public void Both_sessions_holding_audio_at_the_start_are_in_the_call()
    {
        using var presence = Create();

        presence.ReportAudioAcquired("teams-a");
        presence.ReportAudioAcquired("teams-b");
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(CallState.InCall, presence.State);
        Assert.Equal(2, presence.CallSessions.Count);
    }

    // ---- source session excluded from Busy --------------------------------------------------

    [Fact]
    public void The_session_you_are_talking_in_is_never_set_busy()
    {
        using var presence = InCall("teams-a");

        var targets = presence.BusyTargets(["teams-a", "teams-b", "teams-c"]);

        Assert.DoesNotContain("teams-a", targets);
        Assert.Equal(["teams-b", "teams-c"], targets);
    }

    [Fact]
    public void Every_session_in_the_call_is_excluded_not_only_the_source()
    {
        using var presence = InCall("teams-a");
        presence.ReportAudioAcquired("teams-b");

        var targets = presence.BusyTargets(["teams-a", "teams-b", "teams-c"]);

        Assert.Equal(["teams-c"], targets);
    }

    [Fact]
    public void Nothing_is_set_busy_while_idle()
    {
        using var presence = Create();

        Assert.Empty(presence.BusyTargets(["teams-a", "teams-b"]));
    }

    [Fact]
    public void Targets_still_apply_during_the_quiet_window()
    {
        // Presence must not be released early, or a headset swap unsets Busy for ten seconds.
        using var presence = InCall("teams-a");
        presence.ReportAudioReleased("teams-a");

        Assert.Equal(["teams-b"], presence.BusyTargets(["teams-a", "teams-b"]));
    }

    // ---- clear on return to Idle ------------------------------------------------------------

    [Fact]
    public void Returning_to_idle_clears_the_call_and_its_targets()
    {
        using var presence = InCall("teams-a");
        var states = Record(presence);

        presence.ReportAudioReleased("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(CallState.Idle, presence.State);
        Assert.Null(presence.SourceSessionId);
        Assert.Empty(presence.CallSessions);
        Assert.Empty(presence.BusyTargets(["teams-a", "teams-b"]));
        Assert.Equal([CallState.Clearing, CallState.Idle], states);
    }

    [Fact]
    public void A_full_call_raises_in_call_then_idle()
    {
        using var presence = Create();
        var states = Record(presence);

        presence.ReportAudioAcquired("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(3));
        presence.ReportAudioReleased("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal([CallState.InCall, CallState.Clearing, CallState.Idle], states);
    }

    // ---- counting, not toggling -------------------------------------------------------------

    [Fact]
    public void Two_streams_in_one_session_need_two_releases()
    {
        // Teams runs across frames, each with its own copy of bridge.js, and a page can hold
        // more than one stream. Acquisitions are counted, not treated as a toggle.
        using var presence = Create();

        presence.ReportAudioAcquired("teams-a");
        presence.ReportAudioAcquired("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(3));

        presence.ReportAudioReleased("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(CallState.InCall, presence.State);

        presence.ReportAudioReleased("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CallState.Idle, presence.State);
    }

    [Fact]
    public void An_unmatched_release_does_not_go_negative()
    {
        using var presence = Create();

        presence.ReportAudioReleased("teams-a");
        presence.ReportAudioAcquired("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(CallState.InCall, presence.State);
    }

    // ---- a session disappearing mid-call ----------------------------------------------------

    [Fact]
    public void Closing_a_tab_mid_call_does_not_leave_the_state_machine_stuck()
    {
        using var presence = InCall("teams-a");

        presence.ReportSessionGone("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(CallState.Idle, presence.State);
    }

    [Fact]
    public void Closing_one_of_two_tabs_leaves_the_call_running()
    {
        using var presence = InCall("teams-a");
        presence.ReportAudioAcquired("teams-b");

        presence.ReportSessionGone("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(CallState.InCall, presence.State);
        Assert.Equal(["teams-c"], presence.BusyTargets(["teams-b", "teams-c"]));
    }

    // ---- delays changed by a config reload ---------------------------------------------------

    [Fact]
    public void A_reloaded_detect_delay_governs_the_next_call()
    {
        using var presence = Create();
        presence.Apply(Config with { CallDetectDelaySeconds = 8 });

        presence.ReportAudioAcquired("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(CallState.Idle, presence.State);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CallState.InCall, presence.State);
    }

    [Fact]
    public void Changing_the_detect_delay_mid_count_does_not_re_arm_the_timer()
    {
        // Saving settings while a call is starting must not push the call back, or an unrelated
        // edit becomes a way to lose the first few seconds of every meeting.
        using var presence = Create();

        presence.ReportAudioAcquired("teams-a");
        _clock.Advance(TimeSpan.FromSeconds(2));
        presence.Apply(Config with { CallDetectDelaySeconds = 600 });

        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(CallState.InCall, presence.State);
    }

    [Fact]
    public void Changing_the_clear_delay_mid_clear_does_not_push_the_call_out()
    {
        using var presence = InCall("teams-a");

        presence.ReportAudioReleased("teams-a");
        Assert.Equal(CallState.Clearing, presence.State);

        _clock.Advance(TimeSpan.FromSeconds(5));
        presence.Apply(Config with { CallClearDelaySeconds = 600 });
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(CallState.Idle, presence.State);
    }

    private PresenceCoordinator Create() => new(Config, _clock);

    private PresenceCoordinator InCall(string sessionId)
    {
        var presence = Create();
        presence.ReportAudioAcquired(sessionId);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(CallState.InCall, presence.State);
        return presence;
    }

    private static List<CallState> Record(PresenceCoordinator presence)
    {
        var states = new List<CallState>();
        presence.StateChanged += (_, e) => states.Add(e.Current);
        return states;
    }
}
