using Microsoft.Extensions.Time.Testing;
using Muster.Core.Media;

namespace Muster.Core.Tests;

/// <summary>
/// The mic meter is the thing the user glances at mid-call to answer "is anyone hearing me". It
/// has to distinguish three states a bare level number cannot: muted, open but quiet, and live.
/// </summary>
public sealed class MicSignalMonitorTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-15T09:00:00Z"));

    [Fact]
    public void Nothing_capturing_says_nothing()
    {
        var monitor = Create();

        Assert.Equal(MicSignal.Unknown, monitor.Signal);
        Assert.Equal(0, monitor.Level);
        Assert.Null(monitor.LastHeard);
    }

    [Fact]
    public void Sound_above_the_threshold_is_live()
    {
        var monitor = Create();

        monitor.Report("teams-a", level: 0.4, enabled: true);

        Assert.Equal(MicSignal.Live, monitor.Signal);
        Assert.Equal(0.4, monitor.Level);
        Assert.Equal(_clock.GetUtcNow(), monitor.LastHeard);
    }

    [Fact]
    public void An_open_microphone_hearing_nothing_is_quiet_not_broken()
    {
        // Listening in a meeting. Distinct from Unknown, because something is definitely capturing.
        var monitor = Create();

        monitor.Report("teams-a", level: 0.001, enabled: true);

        Assert.Equal(MicSignal.Quiet, monitor.Signal);
        Assert.Null(monitor.LastHeard);
    }

    [Fact]
    public void A_disabled_track_is_muted_rather_than_silent()
    {
        // Teams' mute button leaves the track open and feeds digital silence. Reporting that as a
        // dead microphone is the single most misleading thing this could do.
        var monitor = Create();

        monitor.Report("teams-a", level: 0, enabled: false);

        Assert.Equal(MicSignal.Muted, monitor.Signal);
    }

    [Fact]
    public void A_muted_track_reports_no_level_even_if_the_page_sends_one()
    {
        var monitor = Create();

        monitor.Report("teams-a", level: 0.8, enabled: false);

        Assert.Equal(0, monitor.Level);
        Assert.Null(monitor.LastHeard);
    }

    [Fact]
    public void Sound_keeps_the_meter_live_through_the_gaps_between_words()
    {
        var monitor = Create();
        monitor.Report("teams-a", level: 0.4, enabled: true);

        _clock.Advance(TimeSpan.FromSeconds(1));
        monitor.Report("teams-a", level: 0, enabled: true);

        Assert.Equal(MicSignal.Live, monitor.Signal);
        Assert.Equal(0, monitor.Level);
    }

    [Fact]
    public void Silence_past_the_hearing_window_falls_back_to_quiet()
    {
        var monitor = Create();
        var heard = _clock.GetUtcNow();
        monitor.Report("teams-a", level: 0.4, enabled: true);

        _clock.Advance(TimeSpan.FromSeconds(10));
        monitor.Report("teams-a", level: 0, enabled: true);

        Assert.Equal(MicSignal.Quiet, monitor.Signal);

        // Still reported, because "last heard ten seconds ago" is the useful part of a quiet meter.
        Assert.Equal(heard, monitor.LastHeard);
    }

    [Fact]
    public void One_frame_hearing_sound_outranks_another_sitting_muted()
    {
        // Teams runs across several frames, each with its own copy of bridge.js and its own view
        // of the same call. The loudest honest answer wins.
        var monitor = Create();

        monitor.Report("teams-a", level: 0, enabled: false);
        monitor.Report("teams-b", level: 0.5, enabled: true);

        Assert.Equal(MicSignal.Live, monitor.Signal);
        Assert.Equal(0.5, monitor.Level);
    }

    [Fact]
    public void The_level_is_the_loudest_session_not_the_last_one()
    {
        var monitor = Create();

        monitor.Report("teams-a", level: 0.6, enabled: true);
        monitor.Report("teams-b", level: 0.1, enabled: true);

        Assert.Equal(0.6, monitor.Level);
    }

    [Fact]
    public void Forgetting_the_last_session_returns_to_unknown()
    {
        var monitor = Create();
        monitor.Report("teams-a", level: 0.4, enabled: true);

        monitor.Forget("teams-a");

        Assert.Equal(MicSignal.Unknown, monitor.Signal);
        Assert.Equal(0, monitor.Level);
        Assert.Null(monitor.LastHeard);
    }

    [Fact]
    public void Forgetting_one_of_two_leaves_the_other_reporting()
    {
        var monitor = Create();
        monitor.Report("teams-a", level: 0.4, enabled: true);
        monitor.Report("teams-b", level: 0, enabled: false);

        monitor.Forget("teams-a");

        Assert.Equal(MicSignal.Muted, monitor.Signal);
    }

    [Fact]
    public void Forgetting_a_session_that_never_reported_changes_nothing()
    {
        var monitor = Create();
        var raised = 0;
        monitor.Changed += (_, _) => raised++;

        monitor.Forget("never-seen");

        Assert.Equal(0, raised);
        Assert.Equal(MicSignal.Unknown, monitor.Signal);
    }

    [Fact]
    public void Reset_drops_every_session()
    {
        var monitor = Create();
        monitor.Report("teams-a", level: 0.4, enabled: true);
        monitor.Report("teams-b", level: 0.2, enabled: true);

        monitor.Reset();

        Assert.Equal(MicSignal.Unknown, monitor.Signal);
    }

    [Fact]
    public void Every_report_raises_changed_because_the_meter_follows_the_voice()
    {
        // Not only on a state change: a bar that redrew only when Live/Quiet flipped would sit
        // still while someone talked.
        var monitor = Create();
        var levels = new List<double>();
        monitor.Changed += (_, e) => levels.Add(e.Level);

        monitor.Report("teams-a", level: 0.4, enabled: true);
        monitor.Report("teams-a", level: 0.5, enabled: true);
        monitor.Report("teams-a", level: 0.2, enabled: true);

        Assert.Equal([0.4, 0.5, 0.2], levels);
    }

    [Fact]
    public void A_level_beyond_the_range_is_clamped_rather_than_trusted()
    {
        // Everything here came from page script, which is untrusted input like any other.
        var monitor = Create();

        monitor.Report("teams-a", level: 7.5, enabled: true);

        Assert.Equal(1, monitor.Level);
    }

    private MicSignalMonitor Create() => new(_clock, hearingWindow: TimeSpan.FromSeconds(2.5));
}
