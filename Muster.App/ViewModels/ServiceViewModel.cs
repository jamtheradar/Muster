using Muster.Core.Config;
using Muster.Core.Hosting;

namespace Muster.App.ViewModels;

/// <summary>A pinned service, declared in muster.json and present for the life of the app.</summary>
public sealed class ServiceViewModel : TabViewModel
{
    public ServiceViewModel(WorkspaceConfig workspace, ServiceConfig service)
        : base(SessionDescriptor.ForService(workspace, service))
    {
        Kind = service.Kind;
        KeepAlive = service.KeepAlive;
        Muted = service.Muted;
        NotificationsEnabled = service.NotificationsEnabled;
    }

    public ServiceKind Kind { get; }

    /// <summary>Loaded at launch and never suspended.</summary>
    public bool KeepAlive { get; }

    public bool Muted { get; }

    public bool NotificationsEnabled { get; }

    public override bool CanClose => false;

    // A service the user has told us not to notify about should not badge either. Muted is a
    // different thing: it silences the toast, but the count still shows.
    public override bool TracksUnread => NotificationsEnabled;

    public override bool AcceptsNotifications => NotificationsEnabled && !Muted;
}
