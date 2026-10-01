using CommunityToolkit.Mvvm.ComponentModel;
using Muster.Core.Hosting;
using Muster.Core.Notifications;

namespace Muster.App.ViewModels;

/// <summary>
/// One entry in the tab strip, pinned or ephemeral. Holds no WebView2 reference: the view layer
/// owns the controls, the view model owns the state.
/// </summary>
public abstract partial class TabViewModel : ObservableObject
{
    protected TabViewModel(SessionDescriptor descriptor)
    {
        Descriptor = descriptor;
        DisplayName = descriptor.Name;
    }

    public SessionDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public string ProfileName => Descriptor.ProfileName;

    /// <summary>Label shown on the tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleLabel))]
    public partial string DisplayName { get; set; }

    /// <summary>Live document title. Milestone 4 parses the unread count out of this.</summary>
    [ObservableProperty]
    public partial string? Title { get; set; }

    /// <summary>False until the session has actually been created.</summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; }

    /// <summary>
    /// True while this tab's page is rendering audio, from WebView2's own
    /// <c>IsDocumentPlayingAudio</c>. Nothing is injected for it and nothing is parsed.
    /// </summary>
    /// <remarks>
    /// The speaker half of the device check, always on: if Teams is playing and you hear nothing,
    /// the fault is between the browser and your ears rather than in the call. Under RDP that is
    /// where it usually is.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleLabel))]
    public partial bool IsPlayingAudio { get; set; }

    /// <summary>Unread count parsed from the page title. See SPEC section 7.1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    [NotifyPropertyChangedFor(nameof(AccessibleLabel))]
    public partial int UnreadCount { get; set; }

    public string BadgeText => TitleUnreadParser.Format(UnreadCount);

    public bool HasUnread => UnreadCount > 0;

    /// <summary>
    /// What a screen reader announces. The badge is drawn inside a control template, so without
    /// this the count is invisible to assistive tech.
    /// </summary>
    public string AccessibleLabel => (HasUnread, IsPlayingAudio) switch
    {
        (true, true) => $"{DisplayName}, {UnreadCount} unread, playing audio",
        (true, false) => $"{DisplayName}, {UnreadCount} unread",
        (false, true) => $"{DisplayName}, playing audio",
        _ => DisplayName,
    };

    /// <summary>Pinned services cannot be closed; they are removed by editing the config.</summary>
    public abstract bool CanClose { get; }

    /// <summary>
    /// False for a service with <c>notificationsEnabled: false</c>. Such a tab still runs, it
    /// just never contributes to a badge.
    /// </summary>
    public virtual bool TracksUnread => true;

    /// <summary>
    /// False for a service that is muted or has notifications turned off. Applied at ingestion,
    /// so a muted service never reaches the toast or the history.
    /// </summary>
    public virtual bool AcceptsNotifications => true;

    /// <summary>
    /// Hook for subclasses. The generated <c>OnTitleChanged</c> partial belongs to this class, so
    /// derived types cannot implement it themselves.
    /// </summary>
    protected virtual void OnTitleUpdated(string? title)
        => UnreadCount = TracksUnread ? TitleUnreadParser.Parse(title) : 0;

    partial void OnTitleChanged(string? value) => OnTitleUpdated(value);
}
