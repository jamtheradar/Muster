using CommunityToolkit.Mvvm.ComponentModel;
using Muster.Core.Config;
using Muster.Core.Services;

namespace Muster.App.ViewModels;

/// <summary>
/// One service being edited in the settings screen. Everything is held as text and turned back
/// into a <see cref="ServiceConfig"/> on save, so a half-typed URL is a validation message rather
/// than a binding exception.
/// </summary>
public sealed partial class ServiceEditorViewModel : ObservableObject
{
    public ServiceEditorViewModel(ServiceConfig service)
    {
        Id = service.Id;
        Name = service.Name;
        Kind = service.Kind;
        Url = service.Url.ToString();
        ProfileName = service.ProfileName ?? string.Empty;
        KeepAlive = service.KeepAlive;
        NotificationsEnabled = service.NotificationsEnabled;
        Muted = service.Muted;

        // Remembered rather than inferred, so a service that has no presence block in the file
        // does not silently grow one the first time the settings screen saves.
        PresenceConfigured = service.Presence is not null;
        PresenceStrategy = service.Presence?.Strategy ?? PresenceStrategyKind.None;
        UserPrincipalName = service.Presence?.UserPrincipalName ?? string.Empty;
        TenantId = service.Presence?.TenantId ?? string.Empty;
    }

    /// <summary>A blank service, ready to be filled in. Ids must be unique across every workspace.</summary>
    public static ServiceEditorViewModel Create(string suggestedId) => new(new ServiceConfig
    {
        Id = suggestedId,
        Name = "New service",
        Url = new Uri("https://example.com/"),
    });

    public static IReadOnlyList<ServiceKind> Kinds { get; } = Enum.GetValues<ServiceKind>();

    /// <summary>
    /// Deliberately not every enum value: <see cref="PresenceStrategyKind.Dom"/> is reserved in the
    /// schema and rejected by the validator, so offering it here would only produce a config that
    /// refuses to save.
    /// </summary>
    public static IReadOnlyList<PresenceStrategyKind> Strategies { get; } =
        [PresenceStrategyKind.None, PresenceStrategyKind.Graph, PresenceStrategyKind.Page];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial string Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTeams))]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial ServiceKind Kind { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    /// <summary>Blank means "use the workspace profile", which is the normal case.</summary>
    [ObservableProperty]
    public partial string ProfileName { get; set; }

    [ObservableProperty]
    public partial bool KeepAlive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeBehaviourText))]
    public partial bool NotificationsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeBehaviourText))]
    public partial bool Muted { get; set; }

    /// <summary>Whether this service writes a <c>presence</c> block at all.</summary>
    [ObservableProperty]
    public partial bool PresenceConfigured { get; set; }

    [ObservableProperty]
    public partial PresenceStrategyKind PresenceStrategy { get; set; }

    [ObservableProperty]
    public partial string UserPrincipalName { get; set; }

    [ObservableProperty]
    public partial string TenantId { get; set; }

    /// <summary>Teams is the only kind that gets bespoke logic. See SPEC section 2.</summary>
    public bool IsTeams => Kind == ServiceKind.Teams;

    /// <summary>What the list on the left shows.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>
    /// Spells out the muted/notificationsEnabled distinction at the point of use, because the two
    /// look interchangeable and are not.
    /// </summary>
    public string BadgeBehaviourText => (NotificationsEnabled, Muted) switch
    {
        (false, _) => "No toast, no history, no badge.",
        (true, true) => "No toast, no history, but the unread badge still shows.",
        (true, false) => "Toast, history and badge.",
    };

    /// <summary>
    /// Builds the config record, adding a message per problem rather than throwing. Returns null
    /// only when the entry cannot be represented at all.
    /// </summary>
    public ServiceConfig? TryBuild(string workspaceLabel, ICollection<string> problems)
    {
        var where = $"service '{(string.IsNullOrWhiteSpace(Id) ? Label : Id)}' in {workspaceLabel}";

        if (!UrlNormaliser.TryNormalise(Url, out var url))
        {
            problems.Add($"{where}: '{Url}' is not an http or https URL.");
            return null;
        }

        return new ServiceConfig
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Kind = Kind,
            Url = url,
            ProfileName = Blank(ProfileName),
            KeepAlive = KeepAlive,
            NotificationsEnabled = NotificationsEnabled,
            Muted = Muted,
            Presence = PresenceConfigured
                ? new ServicePresenceConfig
                {
                    Strategy = PresenceStrategy,
                    UserPrincipalName = Blank(UserPrincipalName),
                    TenantId = Blank(TenantId),
                }
                : null,
        };
    }

    partial void OnKindChanged(ServiceKind value)
    {
        // Presence applies to Teams only, and the validator rejects it anywhere else. Dropping it
        // here means changing the kind cannot leave a config that will not load.
        if (value != ServiceKind.Teams)
        {
            PresenceConfigured = false;
        }
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
