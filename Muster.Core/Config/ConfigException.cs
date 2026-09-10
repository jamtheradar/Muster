namespace Muster.Core.Config;

/// <summary>
/// Thrown when <c>muster.json</c> cannot be read or does not validate. The file is hand-edited,
/// so mistakes are reported loudly with every problem listed, never silently patched over.
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string path, IReadOnlyList<string> problems)
        : base($"{path} is not valid:{Environment.NewLine}{string.Join(Environment.NewLine, problems.Select(p => "  - " + p))}")
    {
        Path = path;
        Problems = problems;
    }

    public ConfigException(string path, string message, Exception inner)
        : base($"{path} could not be read: {message}", inner)
    {
        Path = path;
        Problems = [message];
    }

    public string Path { get; }

    public IReadOnlyList<string> Problems { get; }
}
