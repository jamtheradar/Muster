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
            // ConfigureAwait(false) on the stream itself, not only on the read: the implicit
            // DisposeAsync is an await too, and without this it captures whatever context the
            // caller was on. App.OnStartup blocks the UI thread on this to get the logging
            // section before there is a logger, so a captured dispatcher there is a deadlock —
            // and one that presents as the app starting with no window and no log at all.
            var stream = File.OpenRead(Path);

            await using (stream.ConfigureAwait(false))
            {
                config = await JsonSerializer
                    .DeserializeAsync<MusterConfig>(stream, SerializerOptions, ct)
                    .ConfigureAwait(false);
            }
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

        // Same reasoning as the read, and this is the path that actually bit: a first run has no
        // file, so the startup read writes the defaults through here while the UI thread is
        // blocked waiting for it. The .tmp beside the config and nothing else is the fingerprint.
        var stream = File.Create(temporary);

        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, config, SerializerOptions, ct).ConfigureAwait(false);
        }

        File.Move(temporary, Path, overwrite: true);
    }
}
