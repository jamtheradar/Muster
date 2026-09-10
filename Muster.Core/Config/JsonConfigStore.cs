using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muster.Core.Config;

/// <inheritdoc cref="IConfigStore" />
public sealed class JsonConfigStore : IConfigStore
{
    /// <summary>
    /// Shared by the loader, the writer and the golden-file tests, so a round trip is exact.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public JsonConfigStore(string? path = null) => Path = path ?? DefaultPath;

    /// <summary><c>%APPDATA%\Muster</c>.</summary>
    public static string DefaultDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Muster");

    /// <summary><c>%APPDATA%\Muster\muster.json</c>.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(DefaultDirectory, "muster.json");

    public string Path { get; }

    public async Task<MusterConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path))
        {
            var defaults = ConfigDefaults.Create();
            await SaveAsync(defaults, ct).ConfigureAwait(false);
            return defaults;
        }

        MusterConfig? config;
        try
        {
            await using var stream = File.OpenRead(Path);
            config = await JsonSerializer
                .DeserializeAsync<MusterConfig>(stream, SerializerOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ConfigException(Path, ex.Message, ex);
        }

        if (config is null)
        {
            throw new ConfigException(Path, ["the file is empty."]);
        }

        var problems = ConfigValidator.Validate(config);
        if (problems.Count > 0)
        {
            throw new ConfigException(Path, problems);
        }

        return config;
    }

    public async Task SaveAsync(MusterConfig config, CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside the target and move into place, so an interrupted write cannot leave a
        // half-written config that fails to load on next start.
        var temporary = Path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, config, SerializerOptions, ct).ConfigureAwait(false);
        }

        File.Move(temporary, Path, overwrite: true);
    }
}
