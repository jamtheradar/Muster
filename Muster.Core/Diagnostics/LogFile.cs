namespace Muster.Core.Diagnostics;

/// <summary>One log file on disk, as listed in the settings screen.</summary>
/// <param name="Path">Full path, so the row can be opened or revealed in Explorer.</param>
/// <param name="Name">File name only.</param>
/// <param name="Bytes">Size at the time the folder was scanned.</param>
/// <param name="LastWritten">Local last-write time, which is what retention is measured against.</param>
public sealed record LogFile(string Path, string Name, long Bytes, DateTimeOffset LastWritten)
{
    /// <summary>Size rounded for display. Log files are small; three units is plenty.</summary>
    public string SizeText => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024d:0.#} KB",
        _ => $"{Bytes / (1024d * 1024d):0.#} MB",
    };
}
