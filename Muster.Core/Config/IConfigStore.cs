namespace Muster.Core.Config;

/// <summary>Reads and writes <c>muster.json</c>.</summary>
public interface IConfigStore
{
    /// <summary>Full path to the config file, for error messages and the "open config" command.</summary>
    string Path { get; }

    /// <summary>
    /// Loads the config, writing the defaults first if the file does not exist.
    /// </summary>
    /// <exception cref="ConfigException">The file exists but is malformed or invalid.</exception>
    Task<MusterConfig> LoadAsync(CancellationToken ct = default);

    /// <summary>Writes the config atomically.</summary>
    Task SaveAsync(MusterConfig config, CancellationToken ct = default);
}
