using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muster.Core.Config;

namespace Muster.App.ViewModels;

/// <summary>
/// One workspace being edited in the settings screen, with its services underneath it.
/// </summary>
public sealed partial class WorkspaceEditorViewModel : ObservableObject
{
    public WorkspaceEditorViewModel(WorkspaceConfig workspace)
    {
        Id = workspace.Id;
        Name = workspace.Name;
        Abbreviation = workspace.Abbreviation ?? string.Empty;
        Accent = workspace.Accent;
        ProfileName = workspace.ProfileName ?? string.Empty;
        PermissionOriginsText = string.Join(Environment.NewLine, workspace.PermissionOrigins);
        Services = new ObservableCollection<ServiceEditorViewModel>(
            workspace.Services.Select(service => new ServiceEditorViewModel(service)));
        SelectedService = Services.FirstOrDefault();
    }

    /// <summary>A blank workspace with no services in it yet.</summary>
    public static WorkspaceEditorViewModel Create(string suggestedId) => new(new WorkspaceConfig
    {
        Id = suggestedId,
        Name = "New workspace",
    });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(ProfileHint))]
    public partial string Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(RailLabel))]
    public partial string Name { get; set; }

    /// <summary>
    /// What the rail tile shows. Blank falls back to initials from the name, which is fine until
    /// two tenants share a first letter.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailLabel))]
    public partial string Abbreviation { get; set; }

    /// <summary>The wrong-tenant guard. <c>#RRGGBB</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentBrush))]
    public partial string Accent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileHint))]
    public partial string ProfileName { get; set; }

    /// <summary>One host suffix per line, as SPEC section 6.3 describes them.</summary>
    [ObservableProperty]
    public partial string PermissionOriginsText { get; set; }

    public ObservableCollection<ServiceEditorViewModel> Services { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedService))]
    public partial ServiceEditorViewModel? SelectedService { get; set; }

    public bool HasSelectedService => SelectedService is not null;

    public string Label => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>Live preview of the rail tile, so the fallback to initials is visible as you type.</summary>
    public string RailLabel => new WorkspaceConfig { Id = Id, Name = Name, Abbreviation = Blank(Abbreviation) }.RailLabel;

    /// <summary>Live preview of the accent, so a typo in the hex is visible before saving.</summary>
    public Brush AccentBrush
    {
        get
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Accent));
                brush.Freeze();
                return brush;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
            {
                return Brushes.Transparent;
            }
        }
    }

    /// <summary>
    /// Spelled out because changing it orphans the existing profile folder and signs the
    /// workspace out, which is not something to discover after the fact.
    /// </summary>
    public string ProfileHint
    {
        get
        {
            var resolved = string.IsNullOrWhiteSpace(ProfileName) ? $"ws-{Id}" : ProfileName.Trim();
            return $"Sessions run in the {resolved} profile. Changing this signs the workspace out.";
        }
    }

    [RelayCommand]
    private void AddService()
    {
        var service = ServiceEditorViewModel.Create(SuggestServiceId());
        Services.Add(service);
        SelectedService = service;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedService))]
    private void RemoveService()
    {
        if (SelectedService is not { } service)
        {
            return;
        }

        var index = Services.IndexOf(service);
        Services.Remove(service);
        SelectedService = Services.Count == 0 ? null : Services[Math.Min(index, Services.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelectedService))]
    private void MoveServiceUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedService))]
    private void MoveServiceDown() => Move(1);

    /// <summary>Builds the config record, collecting problems rather than throwing.</summary>
    public WorkspaceConfig TryBuild(ICollection<string> problems)
    {
        var label = $"workspace '{(string.IsNullOrWhiteSpace(Id) ? Label : Id)}'";

        var services = Services
            .Select(service => service.TryBuild(label, problems))
            .OfType<ServiceConfig>()
            .ToList();

        var origins = PermissionOriginsText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new WorkspaceConfig
        {
            Id = Id.Trim(),
            Name = Name.Trim(),

            // Null rather than "", so a workspace using initials does not gain an
            // "abbreviation": "" in the file that reads like a deliberate empty label.
            Abbreviation = Blank(Abbreviation),
            Accent = Accent.Trim(),
            ProfileName = string.IsNullOrWhiteSpace(ProfileName) ? null : ProfileName.Trim(),

            // Null rather than an empty list, so a workspace that has never had an allow list
            // does not gain an empty "allowedPermissionOrigins": [] in the file.
            AllowedPermissionOrigins = origins.Count == 0 ? null : origins,
            Services = services,
        };
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Move(int offset)
    {
        if (SelectedService is not { } service)
        {
            return;
        }

        var from = Services.IndexOf(service);
        var to = from + offset;

        if (from < 0 || to < 0 || to >= Services.Count)
        {
            return;
        }

        Services.Move(from, to);
        SelectedService = service;
    }

    private string SuggestServiceId()
    {
        // Service ids are globally unique, so seed with the workspace to make a collision with
        // another workspace's "service-1" less likely. The validator still has the final say.
        var stem = string.IsNullOrWhiteSpace(Id) ? "service" : $"{Id.Trim()}-service";

        for (var n = 1; ; n++)
        {
            var candidate = $"{stem}-{n}";
            if (!Services.Any(s => string.Equals(s.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    partial void OnSelectedServiceChanged(ServiceEditorViewModel? value)
    {
        RemoveServiceCommand.NotifyCanExecuteChanged();
        MoveServiceUpCommand.NotifyCanExecuteChanged();
        MoveServiceDownCommand.NotifyCanExecuteChanged();
    }
}
