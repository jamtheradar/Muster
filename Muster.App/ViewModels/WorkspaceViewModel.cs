using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Muster.Core.Notifications;
using Muster.Core.Config;
using Muster.Core.Hosting;

namespace Muster.App.ViewModels;

/// <summary>
/// One workspace in the left rail. Each remembers its own active tab, so switching away and back
/// returns you to where you were.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    public WorkspaceViewModel(WorkspaceConfig workspace)
    {
        Id = workspace.Id;
        Name = workspace.Name;
        ProfileName = workspace.ResolvedProfileName;
        Accent = ParseAccent(workspace.Accent);
        AccentBrush = new SolidColorBrush(Accent);
        AccentBrush.Freeze();
        Initials = workspace.RailLabel;
        PermissionOrigins = workspace.PermissionOrigins;

        Tabs = new ObservableCollection<TabViewModel>(
            workspace.Services.Select(service => new ServiceViewModel(workspace, service)));

        foreach (var tab in Tabs)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
        }

        Tabs.CollectionChanged += OnTabsChanged;
        ActiveTab = Tabs.FirstOrDefault();
    }

    public string Id { get; }

    public string Name { get; }

    public string ProfileName { get; }

    public Color Accent { get; }

    public SolidColorBrush AccentBrush { get; }

    /// <summary>
    /// What the rail tile shows: the workspace's configured abbreviation, or initials from its
    /// name when it has none.
    /// </summary>
    public string Initials { get; }

    /// <summary>Three characters need a smaller type size to stay inside the 34px tile.</summary>
    public double InitialsFontSize => Initials.Length >= 3 ? 11 : 13;

    /// <summary>Extra hosts this workspace auto-grants permissions to.</summary>
    public IReadOnlyList<string> PermissionOrigins { get; }

    /// <summary>Pinned services first, in config order, then ephemeral tabs as they open.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; }

    public IEnumerable<ServiceViewModel> Services => Tabs.OfType<ServiceViewModel>();

    /// <summary>What the new-window router matches a target URL against.</summary>
    public IEnumerable<SessionDescriptor> PinnedDescriptors => Services.Select(s => s.Descriptor);

    [ObservableProperty]
    public partial TabViewModel? ActiveTab { get; set; }

    /// <summary>Sum of this workspace's tab counts, shown on the rail. See SPEC section 7.2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    [NotifyPropertyChangedFor(nameof(AccessibleLabel))]
    public partial int UnreadCount { get; set; }

    public string BadgeText => TitleUnreadParser.Format(UnreadCount);

    public bool HasUnread => UnreadCount > 0;

    /// <summary>What a screen reader announces for the rail button, badge included.</summary>
    public string AccessibleLabel => HasUnread ? $"{Name}, {UnreadCount} unread" : Name;

    /// <summary>
    /// Takes on an ephemeral tab from the previous instance of this workspace. A config reload
    /// rebuilds the rail from the file, and a tab the user opened themselves is not in the file:
    /// without this, saving a colour would close every link they had open.
    /// </summary>
    public void Adopt(EphemeralTabViewModel tab) => Tabs.Add(tab);

    /// <summary>
    /// Releases the tab subscriptions before this workspace is discarded. An adopted tab outlives
    /// the workspace it was opened in, and would otherwise keep it alive through the handler.
    /// </summary>
    public void Detach()
    {
        Tabs.CollectionChanged -= OnTabsChanged;

        foreach (var tab in Tabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
        }
    }

    /// <summary>Adds an ephemeral tab for <paramref name="target"/> in this workspace's profile.</summary>
    public EphemeralTabViewModel OpenEphemeral(Uri target)
    {
        var tab = new EphemeralTabViewModel(
            SessionDescriptor.ForEphemeral(Id, ProfileName, target, PermissionOrigins));

        Tabs.Add(tab);
        return tab;
    }

    /// <summary>
    /// Removes a tab and picks the next sensible one. Pinned services are never removed here;
    /// they exist for as long as they are in the config.
    /// </summary>
    public void Close(TabViewModel tab)
    {
        if (!tab.CanClose)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.Remove(tab);

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count == 0
                ? null
                : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.OldItems?.OfType<TabViewModel>() ?? [])
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
        }

        foreach (var tab in e.NewItems?.OfType<TabViewModel>() ?? [])
        {
            tab.PropertyChanged += OnTabPropertyChanged;
        }

        // A closed tab takes its unread count with it.
        RecalculateUnread();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.UnreadCount))
        {
            RecalculateUnread();
        }
    }

    private void RecalculateUnread() => UnreadCount = Tabs.Sum(tab => tab.UnreadCount);

    private static Color ParseAccent(string accent)
    {
        // ConfigValidator has already rejected anything that is not #RRGGBB, but a bad colour
        // should never be the reason the shell fails to start.
        try
        {
            return (Color)ColorConverter.ConvertFromString(accent);
        }
        catch (FormatException)
        {
            return Colors.SlateGray;
        }
    }
}
