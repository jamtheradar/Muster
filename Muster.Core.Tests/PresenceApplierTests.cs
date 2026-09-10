using Microsoft.Extensions.Time.Testing;
using Muster.Core.Config;
using Muster.Core.Presence;

namespace Muster.Core.Tests;

/// <summary>
/// The applier is the only thing in the app that changes status other people can see, so the
/// rules it enforces are worth more scrutiny than most: who gets set Busy, who is spared, what
/// happens when an account leaves the config while it is Busy, and what a crash leaves behind.
/// </summary>
public sealed class PresenceApplierTests
{
    private const string Datebyte = "teams-datebyte";
    private const string Fabrikam = "teams-fabrikam";
    private const string ClientB = "teams-clientb";

    [Fact]
    public async Task Nothing_is_written_while_presence_is_disabled()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config(enabled: false));

        await applier.SyncAsync(InCall(Datebyte));

        Assert.Empty(strategy.Writes);
        Assert.Empty(applier.AppliedAccounts);
    }

    [Fact]
    public async Task Entering_a_call_sets_busy_everywhere_except_the_call()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        var outcome = await applier.SyncAsync(InCall(Datebyte));

        Assert.Equal(2, outcome.Set);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal([$"set {Fabrikam}", $"set {ClientB}"], strategy.Writes);
    }

    [Fact]
    public async Task A_session_that_joins_the_call_has_its_busy_cleared()
    {
        // SPEC 8.2 names only the source. The same reasoning applies to a session that joins
        // later: it is in the call, and forcing Busy on the tenant you are talking in is the one
        // outcome nobody wants.
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));
        strategy.Writes.Clear();

        var outcome = await applier.SyncAsync(InCall(Datebyte, Fabrikam));

        Assert.Equal(1, outcome.Cleared);
        Assert.Equal([$"clear {Fabrikam}"], strategy.Writes);
        Assert.Equal([ClientB], applier.AppliedAccounts.Select(a => a.ServiceId));
    }

    [Fact]
    public async Task Clearing_still_counts_as_being_in_a_call()
    {
        // The quiet window exists so a headset swap does not flap presence, which is exactly what
        // dropping Busy the moment audio stopped would do.
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));
        strategy.Writes.Clear();

        await applier.SyncAsync(Change(CallState.Clearing, Datebyte));

        Assert.Empty(strategy.Writes);
        Assert.Equal(2, applier.AppliedAccounts.Count);
    }

    [Fact]
    public async Task Returning_to_idle_clears_everything_that_was_set()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));
        strategy.Writes.Clear();

        var outcome = await applier.SyncAsync(Idle());

        Assert.Equal(2, outcome.Cleared);
        Assert.Equal([$"clear {Fabrikam}", $"clear {ClientB}"], strategy.Writes);
        Assert.Empty(applier.AppliedAccounts);
    }

    [Fact]
    public async Task Syncing_twice_writes_once()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));
        strategy.Writes.Clear();

        await applier.SyncAsync();

        Assert.Empty(strategy.Writes);
    }

    [Fact]
    public async Task An_account_with_no_strategy_is_never_an_account_at_all()
    {
        var config = Config();
        var workspace = config.Workspaces[1];
        var service = workspace.Services[0] with
        {
            Presence = new ServicePresenceConfig { Strategy = PresenceStrategyKind.None },
        };

        config = config with
        {
            Workspaces =
            [
                config.Workspaces[0],
                workspace with { Services = [service] },
                config.Workspaces[2],
            ],
        };

        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, config);

        await applier.SyncAsync(InCall(Datebyte));

        Assert.Equal([$"set {ClientB}"], strategy.Writes);
    }

    [Fact]
    public async Task An_account_removed_from_the_config_while_busy_is_still_cleared()
    {
        // Its details live in the applied set, not the config, precisely so this case works. A
        // deleted account that stays Busy is invisible to everyone except the people it misleads.
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));
        strategy.Writes.Clear();

        var trimmed = Config();
        applier.Apply(trimmed with { Workspaces = [trimmed.Workspaces[0], trimmed.Workspaces[1]] });
        await applier.SyncAsync();

        Assert.Equal([$"clear {ClientB}"], strategy.Writes);
        Assert.Equal([Fabrikam], applier.AppliedAccounts.Select(a => a.ServiceId));
    }

    [Fact]
    public async Task Turning_presence_off_mid_call_clears_what_it_had_set()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));
        strategy.Writes.Clear();

        applier.Apply(Config(enabled: false));
        await applier.SyncAsync();

        Assert.Equal(2, strategy.Writes.Count(write => write.StartsWith("clear", StringComparison.Ordinal)));
        Assert.Empty(applier.AppliedAccounts);
    }

    // ---- manual override --------------------------------------------------------------------

    [Fact]
    public async Task The_manual_override_sets_busy_on_every_account_including_the_call()
    {
        // The case it exists for is a call this app cannot see, so there is no session to spare.
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        var outcome = await applier.SetManualOverrideAsync(true);

        Assert.Equal(3, outcome.Set);
        Assert.True(applier.ManualOverride);
        Assert.Equal(3, applier.AppliedAccounts.Count);
    }

    [Fact]
    public async Task Releasing_the_manual_override_falls_back_to_the_call_state()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));
        await applier.SetManualOverrideAsync(true);
        strategy.Writes.Clear();

        await applier.SetManualOverrideAsync(false);

        // Still on the call, so only the source comes back off Busy.
        Assert.Equal([$"clear {Datebyte}"], strategy.Writes);
        Assert.Equal(2, applier.AppliedAccounts.Count);
    }

    // ---- failure ----------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_set_is_not_recorded_as_applied()
    {
        var strategy = new RecordingStrategy { FailSet = true };
        using var applier = Applier(strategy, Config());

        var outcome = await applier.SyncAsync(InCall(Datebyte));

        Assert.Equal(0, outcome.Set);
        Assert.Equal(2, outcome.Failed);
        Assert.Empty(applier.AppliedAccounts);
    }

    [Fact]
    public async Task A_failed_clear_stays_applied_so_it_can_be_retried()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        await applier.SyncAsync(InCall(Datebyte));

        strategy.FailClear = true;
        var outcome = await applier.SyncAsync(Idle());

        Assert.Equal(0, outcome.Cleared);
        Assert.Equal(2, outcome.Failed);
        Assert.Equal(2, applier.AppliedAccounts.Count);
    }

    [Fact]
    public async Task A_strategy_that_throws_is_a_failure_not_a_crash()
    {
        var strategy = new RecordingStrategy { Throw = true };
        using var applier = Applier(strategy, Config());

        var outcome = await applier.SyncAsync(InCall(Datebyte));

        Assert.Equal(2, outcome.Failed);
        Assert.Empty(applier.AppliedAccounts);
    }

    [Fact]
    public async Task An_account_whose_strategy_is_not_registered_fails_rather_than_pretending()
    {
        using var applier = new PresenceApplier([], new FakeJournal(), new FakeTimeProvider());
        applier.Apply(Config());

        var outcome = await applier.SyncAsync(InCall(Datebyte));

        Assert.Equal(2, outcome.Failed);
        Assert.Empty(applier.AppliedAccounts);
    }

    // ---- renewal ----------------------------------------------------------------------------

    [Fact]
    public async Task Busy_is_re_sent_before_the_expiry_runs_out()
    {
        // Otherwise a meeting longer than expirationDuration drops back to Available mid-sentence,
        // with nothing anywhere to say why.
        var clock = new FakeTimeProvider();
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config(), clock);

        await applier.SyncAsync(InCall(Datebyte));
        strategy.Writes.Clear();

        clock.Advance(TimeSpan.FromMinutes(61));
        await Task.Delay(50);

        Assert.Equal([$"set {Fabrikam}", $"set {ClientB}"], strategy.Writes);
        Assert.Equal(2, applier.AppliedAccounts.Count);
    }

    [Fact]
    public async Task Renewal_stops_once_nothing_is_applied()
    {
        var clock = new FakeTimeProvider();
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config(), clock);

        await applier.SyncAsync(InCall(Datebyte));
        await applier.SyncAsync(Idle());
        strategy.Writes.Clear();

        clock.Advance(TimeSpan.FromHours(4));
        await Task.Delay(50);

        Assert.Empty(strategy.Writes);
    }

    [Theory]
    [InlineData("PT2H", 60)]
    [InlineData("PT30M", 15)]
    [InlineData("PT4M", 5)]
    [InlineData("P1D", 60)]
    [InlineData("not a duration", 5)]
    public void The_renewal_interval_is_half_the_expiry_within_sane_bounds(string expiry, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), PresenceApplier.RenewalInterval(expiry));

    // ---- crash recovery ---------------------------------------------------------------------

    [Fact]
    public async Task A_busy_left_by_a_previous_run_is_cleared_at_startup()
    {
        // The case this exists for: a crash mid-call, leaving someone Busy across every client
        // tenant with nothing running to undo it. See SPEC section 12.
        var journal = new FakeJournal
        {
            Entries = [Account(Fabrikam, "fabrikam"), Account(ClientB, "clientb")],
        };

        var strategy = new RecordingStrategy();
        using var applier = new PresenceApplier([strategy], journal, new FakeTimeProvider());
        applier.Apply(Config());

        var outcome = await applier.RecoverAsync();

        Assert.Equal(2, outcome.Cleared);
        Assert.Equal([$"clear {Fabrikam}", $"clear {ClientB}"], strategy.Writes);
        Assert.Empty(applier.AppliedAccounts);
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task Recovery_clears_an_account_the_config_no_longer_describes()
    {
        // The journal holds whole accounts rather than ids for exactly this: the config can be
        // edited while the app is dead, and the leftover Busy is still real.
        var journal = new FakeJournal { Entries = [Account("teams-gone", "gone")] };
        var strategy = new RecordingStrategy();
        using var applier = new PresenceApplier([strategy], journal, new FakeTimeProvider());
        applier.Apply(Config());

        await applier.RecoverAsync();

        Assert.Equal(["clear teams-gone"], strategy.Writes);
    }

    [Fact]
    public async Task An_empty_journal_makes_no_calls_at_all()
    {
        var strategy = new RecordingStrategy();
        using var applier = Applier(strategy, Config());

        var outcome = await applier.RecoverAsync();

        Assert.False(outcome.DidAnything);
        Assert.Empty(strategy.Writes);
    }

    [Fact]
    public async Task The_journal_records_what_is_busy_so_a_crash_is_recoverable()
    {
        var journal = new FakeJournal();
        var strategy = new RecordingStrategy();
        using var applier = new PresenceApplier([strategy], journal, new FakeTimeProvider());
        applier.Apply(Config());

        await applier.SyncAsync(InCall(Datebyte));

        Assert.Equal([Fabrikam, ClientB], journal.Entries.Select(a => a.ServiceId));

        await applier.SyncAsync(Idle());

        Assert.Empty(journal.Entries);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static PresenceApplier Applier(
        IPresenceStrategy strategy,
        MusterConfig config,
        TimeProvider? clock = null)
    {
        var applier = new PresenceApplier([strategy], new FakeJournal(), clock ?? new FakeTimeProvider());
        applier.Apply(config);
        return applier;
    }

    /// <summary>Three tenants, each with a Teams service on the graph strategy.</summary>
    private static MusterConfig Config(bool enabled = true) => new()
    {
        Presence = new PresenceConfig { Enabled = enabled, ClientId = Guid.Empty.ToString() },
        Workspaces =
        [
            Workspace("datebyte", Datebyte),
            Workspace("fabrikam", Fabrikam),
            Workspace("clientb", ClientB),
        ],
    };

    private static WorkspaceConfig Workspace(string id, string serviceId) => new()
    {
        Id = id,
        Name = id,
        Services =
        [
            new ServiceConfig
            {
                Id = serviceId,
                Name = "Teams",
                Kind = ServiceKind.Teams,
                Url = new Uri("https://teams.cloud.microsoft/"),
                Presence = new ServicePresenceConfig
                {
                    Strategy = PresenceStrategyKind.Graph,
                    UserPrincipalName = $"james@{id}.example",
                    TenantId = id,
                },
            },
        ],
    };

    private static PresenceAccount Account(string serviceId, string workspaceId) => new(
        serviceId,
        workspaceId,
        workspaceId,
        PresenceStrategyKind.Graph,
        $"james@{workspaceId}.example",
        workspaceId);

    private static PresenceStateChangedEventArgs InCall(params string[] sessions)
        => Change(CallState.InCall, sessions);

    private static PresenceStateChangedEventArgs Idle()
        => new(CallState.Clearing, CallState.Idle, null, new HashSet<string>(StringComparer.Ordinal));

    private static PresenceStateChangedEventArgs Change(CallState state, params string[] sessions)
        => new(
            CallState.Idle,
            state,
            sessions.FirstOrDefault(),
            new HashSet<string>(sessions, StringComparer.Ordinal));

    private sealed class RecordingStrategy : IPresenceStrategy
    {
        public List<string> Writes { get; } = [];

        public bool FailSet { get; set; }

        public bool FailClear { get; set; }

        public bool Throw { get; set; }

        public PresenceStrategyKind Kind => PresenceStrategyKind.Graph;

        public Task<bool> SetBusyAsync(PresenceAccount account, string expirationDuration, CancellationToken ct)
        {
            if (Throw)
            {
                throw new InvalidOperationException("strategies are meant to report, not throw");
            }

            Assert.False(string.IsNullOrWhiteSpace(expirationDuration));

            if (FailSet)
            {
                return Task.FromResult(false);
            }

            Writes.Add($"set {account.ServiceId}");
            return Task.FromResult(true);
        }

        public Task<bool> ClearAsync(PresenceAccount account, CancellationToken ct)
        {
            if (Throw)
            {
                throw new InvalidOperationException("strategies are meant to report, not throw");
            }

            if (FailClear)
            {
                return Task.FromResult(false);
            }

            Writes.Add($"clear {account.ServiceId}");
            return Task.FromResult(true);
        }
    }

    private sealed class FakeJournal : IPresenceJournal
    {
        public IReadOnlyList<PresenceAccount> Entries { get; set; } = [];

        public IReadOnlyList<PresenceAccount> Read() => Entries;

        public void Write(IEnumerable<PresenceAccount> accounts) => Entries = accounts.ToList();
    }
}
