using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace Muster.App.Diagnostics;

/// <summary>
/// What this build is, for the About screen and for anyone reading a bug report. Everything here
/// is resolved once at first use and never changes for the life of the process.
/// </summary>
/// <remarks>
/// Every member answers rather than throws. An About screen that cannot render because one of its
/// lines could not be determined is worse than an About screen that says "unknown" — and this is
/// the screen people are sent to when something is already wrong.
/// </remarks>
public static class BuildInfo
{
    /// <summary>
    /// The informational version, which carries any suffix the build stamped on. Falls back to the
    /// file version, then to the assembly version.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>
    /// When the exe was written. Not the commit date and not claimed to be: it is the honest
    /// answer to "is the thing I am running the thing I just built", which is the question this
    /// line exists for.
    /// </summary>
    public static string BuiltOn { get; } = ResolveBuiltOn();

    public static string Copyright { get; } = ResolveCopyright();

    /// <summary>
    /// The WebView2 runtime installed on this machine, or a plain statement that there is not
    /// one. The static form needs no environment, so it still answers when the reason a session
    /// will not start is that the runtime is missing.
    /// </summary>
    public static string WebViewRuntime { get; } = ResolveWebViewRuntime();

    /// <summary>Where the per-workspace profiles live, one folder each.</summary>
    public static string ProfileFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Muster",
        "WebView2");

    private static string ResolveVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // The SDK appends "+<commit sha>" when the repo is available. Useful in a log
                // line, noise in a heading.
                var plus = informational.IndexOf('+');
                return plus < 0 ? informational : informational[..plus];
            }

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ResolveBuiltOn()
    {
        try
        {
            var exe = Environment.ProcessPath;

            return string.IsNullOrEmpty(exe) || !File.Exists(exe)
                ? "unknown"
                : File.GetLastWriteTime(exe).ToString("d MMM yyyy, HH:mm");
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ResolveCopyright()
    {
        try
        {
            var exe = Environment.ProcessPath;

            var attribute = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

            if (!string.IsNullOrWhiteSpace(attribute))
            {
                return attribute;
            }

            return string.IsNullOrEmpty(exe)
                ? string.Empty
                : FileVersionInfo.GetVersionInfo(exe).LegalCopyright ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ResolveWebViewRuntime()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();

            return string.IsNullOrWhiteSpace(version)
                ? "not installed"
                : version;
        }
        catch (Exception)
        {
            // The runtime being absent throws rather than returning null, and that is exactly the
            // case this line is worth reading in.
            return "not installed";
        }
    }
}
