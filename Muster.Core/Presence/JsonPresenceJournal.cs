using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muster.Core.Presence;

/// <summary>
/// The journal as a small JSON file in <c>%LOCALAPPDATA%\Muster</c> rather than beside the config:
/// it is app state, not something anyone should be hand-editing.
/// </summary>
/// <remarks>
/// Every operation swallows its own failures. A presence write must never fail because the note
/// about it could not be filed, and the worst case of an unreadable journal is that a crash
/// leaves Busy in place until <c>expirationDuration</c> runs out — which is exactly the case that
/// setting is the safety net for.
/// </remarks>
public sealed class JsonPresenceJournal(string? path = null) : IPresenceJournal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; } = path ?? DefaultPath;

    /// <summary><c>%LOCALAPPDATA%\Muster\presence-applied.json</c>.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Muster",
        "presence-applied.json");

    public IReadOnlyList<PresenceAccount> Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<PresenceAccount>>(File.ReadAllText(Path), Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Write(IEnumerable<PresenceAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(Path, JsonSerializer.Serialize(accounts.ToList(), Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately swallowed. See the class remarks.
        }
    }
}
