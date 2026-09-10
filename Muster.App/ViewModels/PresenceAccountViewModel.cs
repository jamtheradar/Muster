using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muster.Core.Config;
using Muster.Core.Presence;
using Muster.Graph;

namespace Muster.App.ViewModels;

/// <summary>
/// One Teams account in the settings screen's presence list, with its sign-in state.
/// </summary>
/// <remarks>
/// Sign-in lives here rather than anywhere near the call path on purpose: consent is interactive
/// and per tenant, and a sign-in window appearing because a meeting started would be
/// indistinguishable from a phishing prompt. The settings screen is where the user asked for it.
/// </remarks>
public sealed partial class PresenceAccountViewModel(
    PresenceAccount account,
    IGraphTokenProvider tokens) : ObservableObject
{
    public PresenceAccount Account { get; } = account;

    public string Label => Account.Label;

    public string TenantId => Account.TenantId;

    public PresenceStrategyKind Strategy => Account.Strategy;

    /// <summary>
    /// Whether MSAL has a usable account cached. Not a promise that a write will work: consent can
    /// be revoked, and the refresh token can expire, neither of which is visible from here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsBusy { get; set; }

    /// <summary>Set when a sign-in failed, so the reason survives the dialog closing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Problem { get; set; }

    public string StatusText => (IsBusy, Problem, IsSignedIn) switch
    {
        (true, _, _) => "Signing in...",
        (_, { } problem, _) => problem,
        (_, _, true) => "Signed in",
        _ => "Not signed in. Presence cannot be written for this account.",
    };

    public async Task RefreshAsync(CancellationToken ct = default)
        => IsSignedIn = await tokens.IsSignedInAsync(Account, ct).ConfigureAwait(true);

    [RelayCommand]
    private async Task SignInAsync(CancellationToken ct)
    {
        IsBusy = true;
        Problem = null;

        try
        {
            var result = await tokens.SignInAsync(Account, ct).ConfigureAwait(true);

            IsSignedIn = result.Succeeded;
            Problem = result.Succeeded ? null : result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync(CancellationToken ct)
    {
        await tokens.SignOutAsync(Account, ct).ConfigureAwait(true);
        Problem = null;
        IsSignedIn = false;
    }
}
