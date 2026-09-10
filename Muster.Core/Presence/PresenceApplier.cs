using System.Xml;
using Muster.Core.Config;

namespace Muster.Core.Presence;

/// <summary>
/// Turns the call state machine's answer into presence writes. See SPEC sections 8.2 and 8.3.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PresenceCoordinator"/> decides whether you are on a call; this decides what that
/// should mean for every other tenant, and is the only thing in the app that writes presence
/// anywhere. It works by computing the set of accounts that should be showing Busy and reconciling
/// it against the set that already is, which is what makes the awkward cases fall out for free: a
/// second session joining a call stops being a Busy target and is cleared, and an account deleted
/// from the config while Busy is still cleared, because it is still in the applied set.
/// </para>
/// <para>
/// Nothing here logs. Muster.Core takes no logging dependency, so every attempt is raised through
/// <see cref="Applied"/> instead and the host writes the line.
/// </para>
/// </remarks>
public sealed class PresenceApplier : IDisposable
{
    /// <summary>The shortest renewal interval. Below this a long call would be all renewals.</summary>
    public static readonly TimeSpan MinimumRenewalInterval = TimeSpan.FromMinutes(5);

    /// <summary>The longest renewal interval, so a mis-parsed expiry cannot mean never.</summary>
    public static readonly TimeSpan MaximumRenewalInterval = TimeSpan.FromMinutes(60);

    private readonly IReadOnlyDictionary<PresenceStrategyKind, IPresenceStrategy> _strategies;
    private readonly IPresenceJournal _journal;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Accounts this app has Busy right now, keyed by service id. The whole record rather than the
    // id, because clearing one that has since left the config still needs its tenant and UPN.
    private readonly Dictionary<string, PresenceAccount> _applied = new(StringComparer.Ordinal);

    private IReadOnlyList<PresenceAccount> _accounts = [];
    private bool _enabled;
    private string _expiration = new PresenceConfig().ExpirationDuration;
    private CallState _state = CallState.Idle;
    private IReadOnlySet<string> _callSessions = new HashSet<string>(StringComparer.Ordinal);
    private bool _manualOverride;
    private ITimer? _renewal;
    private bool _disposed;

    public PresenceApplier(
        IEnumerable<IPresenceStrategy> strategies,
        IPresenceJournal journal,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        _strategies = strategies.ToDictionary(strategy => strategy.Kind);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised once per write attempt, successful or not, for the host to log.</summary>
    public event EventHandler<PresenceActionEventArgs>? Applied;

    /// <summary>Raised when the set of accounts showing Busy changes, for the indicator.</summary>
    public event EventHandler? AppliedAccountsChanged;

    /// <summary><c>presence.enabled</c>. False means nothing is ever written.</summary>
    public bool Enabled => _enabled;

    /// <summary>Whether the user has forced Busy everywhere by hand. See SPEC section 8.4.</summary>
    public bool ManualOverride => _manualOverride;

    /// <summary>The accounts currently showing Busy because of this app.</summary>
    public IReadOnlyList<PresenceAccount> AppliedAccounts
    {
        get
        {
            lock (_applied)
            {
                return _applied.Values.ToList();
            }
        }
    }

    /// <summary>Every configured account, whether or not it is currently Busy.</summary>
    public IReadOnlyList<PresenceAccount> Accounts => _accounts;

    /// <summary>
    /// Takes settings from a load or reload. Writes nothing: the caller follows with
    /// <see cref="SyncAsync(CancellationToken)"/>, which is also what clears an account the edit
    /// has just removed.
    /// </summary>
    public void Apply(MusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _enabled = config.Presence.Enabled;
        _expiration = config.Presence.ExpirationDuration;
        _accounts = PresenceAccount.From(config);
    }

    /// <summary>Records a call state transition. Follow with <see cref="SyncAsync(CancellationToken)"/>.</summary>
    public void Observe(PresenceStateChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        _state = change.Current;
        _callSessions = change.CallSessions;
    }

    /// <summary>Records a transition and applies it in one step.</summary>
    public Task<PresenceSyncOutcome> SyncAsync(
        PresenceStateChangedEventArgs change,
        CancellationToken ct = default)
    {
        Observe(change);
        return SyncAsync(ct);
    }

    /// <summary>
    /// Brings presence into line with the current call state, config and manual override.
    /// Idempotent: calling it when nothing has changed writes nothing.
    /// </summary>
    public async Task<PresenceSyncOutcome> SyncAsync(CancellationToken ct = default)
        => await ReconcileAsync(Desired(), renew: false, ct).ConfigureAwait(false);

    /// <summary>
    /// Forces Busy on every account, or drops back to whatever the call state says. The escape
    /// hatch for a call in something this app cannot see, and for automatic clearing misbehaving.
    /// </summary>
    public async Task<PresenceSyncOutcome> SetManualOverrideAsync(bool on, CancellationToken ct = default)
    {
        _manualOverride = on;
        return await SyncAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears everything this app has set, whatever the call state. Used on the way out: quitting
    /// while Busy would otherwise leave it in place until the expiry ran out.
    /// </summary>
    public async Task<PresenceSyncOutcome> ClearAllAsync(CancellationToken ct = default)
        => await ReconcileAsync([], renew: false, ct).ConfigureAwait(false);

    /// <summary>
    /// Clears any Busy left behind by a previous run. Called once at startup, before the first
    /// sync: a crash mid-call is exactly the case that leaves someone showing Busy across five
    /// client tenants with nothing running to undo it. See SPEC section 12.
    /// </summary>
    public async Task<PresenceSyncOutcome> RecoverAsync(CancellationToken ct = default)
    {
        var stale = _journal.Read();

        if (stale.Count == 0)
        {
            return PresenceSyncOutcome.Nothing;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            foreach (var account in stale)
            {
                // Through Remember, not TryAdd: AppliedAccounts can be read from the UI thread at
                // any moment, and every mutation of _applied has to take the same lock it does.
                Remember(account);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseAppliedChanged();

        // Deliberately not Desired(): a call cannot be in progress before the shell has loaded,
        // and recovery must not be able to re-set the thing it is here to clear.
        return await ReconcileAsync([], renew: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Who should be showing Busy right now.
    /// </summary>
    /// <remarks>
    /// Every session taking part in the call is excluded, not only the source. SPEC 8.2 names the
    /// source; the same reasoning applies to a session that joins later — it is in the call, and
    /// forcing Busy on the tenant you are actually talking in is the one outcome nobody wants.
    /// Clearing counts as in-call: the quiet window exists so a headset swap does not flap
    /// presence, which is exactly what dropping Busy the moment audio stopped would do.
    /// </remarks>
    private Dictionary<string, PresenceAccount> Desired()
    {
        var desired = new Dictionary<string, PresenceAccount>(StringComparer.Ordinal);

        if (!_enabled)
        {
            return desired;
        }

        var everywhere = _manualOverride;
        var inCall = _state is CallState.InCall or CallState.Clearing;

        if (!everywhere && !inCall)
        {
            return desired;
        }

        foreach (var account in _accounts)
        {
            if (!everywhere && _callSessions.Contains(account.ServiceId))
            {
                continue;
            }

            desired[account.ServiceId] = account;
        }

        return desired;
    }

    private async Task<PresenceSyncOutcome> ReconcileAsync(
        Dictionary<string, PresenceAccount> desired,
        bool renew,
        CancellationToken ct)
    {
        if (_disposed)
        {
            return PresenceSyncOutcome.Nothing;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        var set = 0;
        var cleared = 0;
        var renewed = 0;
        var failed = 0;
        var changed = false;

        try
        {
            foreach (var account in _applied.Values.ToList())
            {
                if (desired.ContainsKey(account.ServiceId))
                {
                    continue;
                }

                if (await ClearOneAsync(account, ct).ConfigureAwait(false))
                {
                    Forget(account.ServiceId);
                    cleared++;
                    changed = true;
                }
                else
                {
                    // Left in the applied set on purpose: it is still Busy as far as anyone knows,
                    // and the renewal tick is what gets to try again.
                    failed++;
                }
            }

            foreach (var account in desired.Values)
            {
                var already = _applied.ContainsKey(account.ServiceId);

                if (already && !renew)
                {
                    continue;
                }

                var action = already ? PresenceAction.Renew : PresenceAction.Set;

                if (await SetOneAsync(account, action, ct).ConfigureAwait(false))
                {
                    Remember(account);

                    if (already)
                    {
                        renewed++;
                    }
                    else
                    {
                        set++;
                        changed = true;
                    }
                }
                else
                {
                    failed++;
                }
            }

            if (changed)
            {
                _journal.Write(AppliedAccounts);
            }

            ScheduleRenewal(_applied.Count > 0 || desired.Count > 0);
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            RaiseAppliedChanged();
        }

        return new PresenceSyncOutcome(set, cleared, renewed, failed);
    }

    private void Remember(PresenceAccount account)
    {
        lock (_applied)
        {
            _applied[account.ServiceId] = account;
        }
    }

    private void Forget(string serviceId)
    {
        lock (_applied)
        {
            _applied.Remove(serviceId);
        }
    }

    private async Task<bool> SetOneAsync(PresenceAccount account, PresenceAction action, CancellationToken ct)
    {
        var succeeded = false;

        try
        {
            succeeded = _strategies.TryGetValue(account.Strategy, out var strategy)
                && await strategy.SetBusyAsync(account, _expiration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A strategy is meant to report failure rather than throw, but presence must never be
            // able to take the shell down with it. Reported as a failure and logged by the host.
        }

        Applied?.Invoke(this, new PresenceActionEventArgs(account, action, succeeded));
        return succeeded;
    }

    private async Task<bool> ClearOneAsync(PresenceAccount account, CancellationToken ct)
    {
        var succeeded = false;

        try
        {
            succeeded = _strategies.TryGetValue(account.Strategy, out var strategy)
                && await strategy.ClearAsync(account, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // See SetOneAsync.
        }

        Applied?.Invoke(this, new PresenceActionEventArgs(account, PresenceAction.Clear, succeeded));
        return succeeded;
    }

    /// <summary>
    /// Re-sends Busy before its expiry runs out, and retries anything that failed.
    /// </summary>
    /// <remarks>
    /// <c>expirationDuration</c> is a safety net against a crash, not a statement about call
    /// length: a meeting that runs past it would otherwise drop back to Available mid-sentence,
    /// with nothing to say so. The interval is half the expiry, which leaves a whole window of
    /// slack for a failed renewal to be retried before anything becomes visible.
    /// </remarks>
    private void ScheduleRenewal(bool wanted)
    {
        if (!wanted || _disposed)
        {
            _renewal?.Dispose();
            _renewal = null;
            return;
        }

        if (_renewal is not null)
        {
            return;
        }

        var interval = RenewalInterval(_expiration);
        _renewal = _clock.CreateTimer(_ => OnRenewalTick(), null, interval, interval);
    }

    /// <summary>How often Busy is re-sent for a given expiry. Public so the rule can be tested.</summary>
    public static TimeSpan RenewalInterval(string expirationDuration)
    {
        TimeSpan expiry;

        try
        {
            expiry = XmlConvert.ToTimeSpan(expirationDuration);
        }
        catch (FormatException)
        {
            // The validator only insists the value is present. A nonsense one still gets renewed,
            // as often as anything else would be, rather than never.
            return MinimumRenewalInterval;
        }

        var half = TimeSpan.FromTicks(expiry.Ticks / 2);

        if (half < MinimumRenewalInterval)
        {
            return MinimumRenewalInterval;
        }

        return half > MaximumRenewalInterval ? MaximumRenewalInterval : half;
    }

    private void OnRenewalTick() => _ = RenewAsync();

    private async Task RenewAsync()
    {
        try
        {
            await ReconcileAsync(Desired(), renew: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A timer callback that throws takes the process with it. Everything meaningful has
            // already been reported through Applied.
        }
    }

    private void RaiseAppliedChanged() => AppliedAccountsChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renewal?.Dispose();
        _renewal = null;
        _gate.Dispose();
    }
}
