using System.Globalization;
using System.Text.Json.Serialization;

namespace Muster.Core.Config;

/// <summary>
/// A named group of services sharing one WebView2 profile, and therefore one set of cookies,
/// storage and auth state. Typically one workspace per client tenant.
/// </summary>
public sealed record WorkspaceConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// One to three characters for the rail tile. Omitted, the tile falls back to initials taken
    /// from <see cref="Name"/>, which collide the moment two tenants start with the same letters.
    /// </summary>
    public string? Abbreviation { get; init; }

    /// <summary>Hex colour, <c>#RRGGBB</c>. The wrong-tenant guard in the UI.</summary>
    public string Accent { get; init; } = "#2D7D9A";

    /// <summary>Overrides the default profile name. Rarely needed.</summary>
    public string? ProfileName { get; init; }

    public IReadOnlyList<ServiceConfig> Services { get; init; } = [];

    /// <summary>
    /// Extra hosts that get microphone, camera and notifications without prompting, on top of
    /// the built-in Microsoft origins. Host suffixes, e.g. <c>contoso.com</c> matches
    /// <c>meet.contoso.com</c>. See SPEC section 6.3.
    /// </summary>
    public IReadOnlyList<string>? AllowedPermissionOrigins { get; init; }

    /// <summary>Never null, for callers that just want to iterate.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> PermissionOrigins => AllowedPermissionOrigins ?? [];

    /// <summary>
    /// What the rail tile shows: the configured <see cref="Abbreviation"/> if there is one,
    /// otherwise initials from the name. Lives here rather than in the view model so the fallback
    /// is testable without a window.
    /// </summary>
    [JsonIgnore]
    public string RailLabel => string.IsNullOrWhiteSpace(Abbreviation)
        ? InitialsOf(Name)
        : Abbreviation.Trim();

    private static string InitialsOf(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpper(CultureInfo.CurrentCulture),
            _ => string.Concat(words[0][..1], words[1][..1]).ToUpper(CultureInfo.CurrentCulture),
        };
    }

    /// <summary>
    /// The WebView2 profile every service in this workspace uses unless it overrides it.
    /// Changing this orphans the existing profile folder and signs the workspace out.
    /// </summary>
    [JsonIgnore]
    public string ResolvedProfileName => string.IsNullOrWhiteSpace(ProfileName)
        ? $"ws-{Id}"
        : ProfileName;

    // Records compare collection members by reference. Config is compared for real: the tests
    // round-trip it, and hot-reload will need to diff two loads to decide what changed.
    public bool Equals(WorkspaceConfig? other)
        => other is not null
        && Id == other.Id
        && Name == other.Name
        && Abbreviation == other.Abbreviation
        && Accent == other.Accent
        && ProfileName == other.ProfileName
        && PermissionOrigins.SequenceEqual(other.PermissionOrigins)
        && Services.SequenceEqual(other.Services);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(Abbreviation);
        hash.Add(Accent);
        hash.Add(ProfileName);
        foreach (var origin in PermissionOrigins)
        {
            hash.Add(origin);
        }

        foreach (var service in Services)
        {
            hash.Add(service);
        }

        return hash.ToHashCode();
    }
}
