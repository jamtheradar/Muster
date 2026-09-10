using Microsoft.Extensions.Time.Testing;
using Muster.Core.Config;
using Muster.Core.Notifications;

namespace Muster.Core.Tests;

public sealed class NotificationHubTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-08-29T09:00:00Z"));

    [Fact]
    public void Ingests_and_keeps_newest_first()
    {
        var hub = new NotificationHub(200, _clock);

        hub.Ingest("teams", "db", "Teams", "First", "one", NotificationSource.WebView);
        _clock.Advance(TimeSpan.FromMinutes(1));
        hub.Ingest("teams", "db", "Teams", "Second", "two", NotificationSource.WebView);

        Assert.Equal(2, hub.History.Count);
        Assert.Equal("Second", hub.History[0].Title);
        Assert.Equal("First", hub.History[1].Title);
    }

    [Fact]
    public void The_same_message_on_both_paths_is_stored_once()
    {
        // The exact case the two ingestion paths create: WebView2 raises it, then the service
        // worker shim reports the same thing a moment later.
        var hub = new NotificationHub(200, _clock);

        var first = hub.Ingest("teams", "db", "Teams", "Alice", "hi", NotificationSource.WebView);
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        var second = hub.Ingest("teams", "db", "Teams", "Alice", "hi", NotificationSource.ServiceWorker);

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);

        // The duplicate still resolves to the stored record, so a late WebView2 handle can be
        // attached to the notification the panel is actually showing.
        Assert.Same(first.Record, second.Record);
        Assert.Single(hub.History);
    }

    [Fact]
    public void An_identical_message_after_the_window_is_a_new_notification()
    {
        // Someone really can send "ok" twice. Past the window it is a separate event.
        var hub = new NotificationHub(200, _clock);

        hub.Ingest("teams", "db", "Teams", "Alice", "ok", NotificationSource.WebView);
        _clock.Advance(NotificationHub.DedupeWindow + TimeSpan.FromSeconds(1));
        var second = hub.Ingest("teams", "db", "Teams", "Alice", "ok", NotificationSource.WebView);

        Assert.True(second.IsNew);
        Assert.Equal(2, hub.History.Count);
    }

    [Fact]
    public void Identical_text_from_different_sessions_is_never_merged()
    {
        // Two tenants, same person, same message. They are different notifications.
        var hub = new NotificationHub(200, _clock);

        hub.Ingest("teams-a", "a", "Teams", "Alice", "hi", NotificationSource.WebView);
        var second = hub.Ingest("teams-b", "b", "Teams", "Alice", "hi", NotificationSource.WebView);

        Assert.True(second.IsNew);
        Assert.Equal(2, hub.History.Count);
    }

    [Fact]
    public void Different_bodies_from_one_session_are_separate()
    {
        var hub = new NotificationHub(200, _clock);

        hub.Ingest("teams", "db", "Teams", "Alice", "first", NotificationSource.WebView);
        var second = hub.Ingest("teams", "db", "Teams", "Alice", "second", NotificationSource.WebView);

        Assert.True(second.IsNew);
        Assert.Equal(2, hub.History.Count);
    }

    [Fact]
    public void History_is_capped_and_drops_the_oldest()
    {
        var hub = new NotificationHub(3, _clock);

        for (var i = 0; i < 6; i++)
        {
            hub.Ingest("teams", "db", "Teams", $"m{i}", $"b{i}", NotificationSource.WebView);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(3, hub.History.Count);
        Assert.Equal("m5", hub.History[0].Title);
        Assert.Equal("m3", hub.History[2].Title);
    }

    [Fact]
    public void Raises_received_only_for_stored_records()
    {
        var hub = new NotificationHub(200, _clock);
        var raised = new List<NotificationRecord>();
        hub.Received += (_, record) => raised.Add(record);

        hub.Ingest("teams", "db", "Teams", "Alice", "hi", NotificationSource.WebView);
        hub.Ingest("teams", "db", "Teams", "Alice", "hi", NotificationSource.ServiceWorker);

        Assert.Single(raised);
    }

    [Fact]
    public void Finds_a_record_by_id_for_toast_activation()
    {
        var hub = new NotificationHub(200, _clock);
        var record = hub.Ingest("teams", "db", "Teams", "Alice", "hi", NotificationSource.WebView).Record;

        Assert.Same(record, hub.Find(record.Id));
        Assert.Null(hub.Find("nope"));
    }

    [Fact]
    public void Clear_empties_history_once()
    {
        var hub = new NotificationHub(200, _clock);
        var cleared = 0;
        hub.Cleared += (_, _) => cleared++;

        hub.Ingest("teams", "db", "Teams", "Alice", "hi", NotificationSource.WebView);
        hub.Clear();
        hub.Clear();

        Assert.Empty(hub.History);
        Assert.Equal(1, cleared);
    }

    // ---- a live history limit ---------------------------------------------------------------

    [Fact]
    public void Lowering_the_history_limit_trims_what_is_already_there()
    {
        // notifications.historyLimit is applied without a restart, so the panel has to be told
        // that the history it is rendering just got shorter.
        var hub = new NotificationHub(10, _clock);
        var trimmed = 0;
        hub.Trimmed += (_, _) => trimmed++;

        for (var i = 0; i < 6; i++)
        {
            hub.Ingest("teams", "db", "Teams", $"m{i}", $"b{i}", NotificationSource.WebView);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        hub.HistoryLimit = 2;

        Assert.Equal(2, hub.History.Count);
        Assert.Equal("m5", hub.History[0].Title);
        Assert.Equal(1, trimmed);
    }

    [Fact]
    public void Raising_the_history_limit_keeps_everything_and_says_nothing()
    {
        var hub = new NotificationHub(2, _clock);
        var trimmed = 0;
        hub.Trimmed += (_, _) => trimmed++;

        hub.Ingest("teams", "db", "Teams", "one", "1", NotificationSource.WebView);
        hub.HistoryLimit = 500;

        Assert.Single(hub.History);
        Assert.Equal(0, trimmed);
        Assert.Equal(500, hub.HistoryLimit);
    }

    [Fact]
    public void The_new_limit_governs_the_next_notification_too()
    {
        var hub = new NotificationHub(50, _clock);
        hub.HistoryLimit = 1;

        hub.Ingest("teams", "db", "Teams", "one", "1", NotificationSource.WebView);
        _clock.Advance(TimeSpan.FromMinutes(1));
        hub.Ingest("teams", "db", "Teams", "two", "2", NotificationSource.WebView);

        Assert.Single(hub.History);
        Assert.Equal("two", hub.History[0].Title);
    }

    [Fact]
    public void A_history_limit_below_one_is_pulled_up_rather_than_emptying_the_panel()
    {
        var hub = new NotificationHub(5, _clock) { HistoryLimit = 0 };

        hub.Ingest("teams", "db", "Teams", "one", "1", NotificationSource.WebView);

        Assert.Equal(1, hub.HistoryLimit);
        Assert.Single(hub.History);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    public void Service_flags_gate_notifications_and_badges(
        bool notificationsEnabled, bool muted, bool expectNotifications, bool expectBadge)
    {
        // Muted silences the interruption but keeps the badge; notificationsEnabled false means
        // the service is ignored entirely.
        var service = new ServiceConfig
        {
            Id = "s",
            Name = "S",
            Url = new Uri("https://example.com/"),
            NotificationsEnabled = notificationsEnabled,
            Muted = muted,
        };

        Assert.Equal(expectNotifications, service.AcceptsNotifications);
        Assert.Equal(expectBadge, service.ContributesUnread);
    }
}
