using CommunityToolkit.Mvvm.ComponentModel;
using Muster.Core.Notifications;

namespace Muster.App.ViewModels;

/// <summary>One row in the notification panel.</summary>
public sealed partial class NotificationItemViewModel : ObservableObject
{
    public NotificationItemViewModel(NotificationRecord record, string workspaceName)
    {
        Record = record;
        WorkspaceName = workspaceName;
        Age = Describe(record.ReceivedAt, DateTimeOffset.UtcNow);
    }

    public NotificationRecord Record { get; }

    public string Title => Record.Title;

    public string Body => Record.Body;

    public string ServiceName => Record.ServiceName;

    public string WorkspaceName { get; }

    /// <summary>The panel groups by workspace then service. See SPEC section 7.2.</summary>
    public string GroupName => $"{WorkspaceName} · {ServiceName}";

    /// <summary>Relative age, refreshed whenever the panel is opened.</summary>
    [ObservableProperty]
    public partial string Age { get; set; }

    public void RefreshAge() => Age = Describe(Record.ReceivedAt, DateTimeOffset.UtcNow);

    private static string Describe(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;

        return elapsed switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h ago",
            _ => at.ToLocalTime().ToString("d MMM HH:mm"),
        };
    }
}
