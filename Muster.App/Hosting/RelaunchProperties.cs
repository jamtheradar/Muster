using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muster.App.Configuration;
using Muster.Core.Config;

namespace Muster.App.Hosting;

/// <summary>
/// Tells Windows what a shortcut to this window should look like, so that pinning it produces the
/// chosen icon rather than the one compiled into the exe.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the problem <see cref="PinnedShortcut"/> cannot solve. Unpinning and
/// repinning does not edit the existing shortcut, it throws it away and builds a new one, and the
/// new one is derived from the exe — so a repin silently undoes any icon written into the old
/// link. Repointing an existing pin and controlling what the next pin is made of are two separate
/// mechanisms, and both are needed for the setting to stick.
/// </para>
/// <para>
/// <c>RelaunchCommand</c> and <c>RelaunchIconResource</c> are the documented window properties the
/// taskbar reads when it builds that shortcut. Nothing on disk is touched: they live on our own
/// window, which is why this one runs automatically while the shortcut rewrite stays behind a
/// button.
/// </para>
/// <para>
/// <c>RelaunchDisplayNameResource</c> is deliberately not set. It takes a resource reference
/// rather than a literal, and a malformed one costs the pin its name — while leaving it out
/// falls back to the exe's own <c>FileDescription</c>, which is already "Muster".
/// </para>
/// </remarks>
public sealed partial class RelaunchProperties(ILogger<RelaunchProperties> log)
{
    /// <summary>The property set every AppUserModel window property belongs to.</summary>
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>A null-terminated wide string owned by the COM task allocator.</summary>
    private const ushort VtLpwstr = 31;

    /// <summary>What a shortcut built from this window should launch.</summary>
    private static readonly PropertyKey RelaunchCommand = new(AppUserModel, 2);

    /// <summary>What icon that shortcut should carry.</summary>
    private static readonly PropertyKey RelaunchIconResource = new(AppUserModel, 3);

    /// <summary>
    /// Applies the properties to <paramref name="handle"/>. Safe to call again whenever the set
    /// changes, and a no-op before the window has a handle. Never throws: a pin that comes out
    /// with the wrong icon is a cosmetic disappointment, not a reason to take the shell down.
    /// </summary>
    public void Apply(IntPtr handle, IconSet set)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        var icon = AppIcons.PathOf(set);

        if (!File.Exists(icon))
        {
            log.LogWarning("Not setting the relaunch icon: {Icon} is missing", icon);
            return;
        }

        var iid = PropertyStoreId;

        try
        {
            var hr = SHGetPropertyStoreForWindow(handle, in iid, out var raw);

            if (hr != 0 || raw == IntPtr.Zero)
            {
                log.LogDebug("No property store for the window, HRESULT {Result:X8}", hr);
                return;
            }

            IPropertyStore? store = null;

            try
            {
                store = (IPropertyStore)Marshal.GetObjectForIUnknown(raw);

                // Quoted: the path can contain spaces, and this is a command line, not a path.
                Set(store, RelaunchCommand, $"\"{exe}\"");

                // Index 0 - a single-image .ico, not an exe carrying several.
                Set(store, RelaunchIconResource, $"{icon},0");

                store.Commit();

                log.LogDebug("A new pin of this window would use {Icon}", icon);
            }
            finally
            {
                if (store is not null)
                {
                    Marshal.FinalReleaseComObject(store);
                }

                Marshal.Release(raw);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not set the relaunch properties");
        }
    }

    /// <summary>
    /// Builds the variant by hand. <c>InitPropVariantFromString</c> looks like the obvious way to
    /// do this and is not available: it is an inline helper in <c>propvarutil.h</c>, not an export
    /// of <c>propsys.dll</c>, so importing it throws <see cref="EntryPointNotFoundException"/> at
    /// the call rather than failing to build. A <c>VT_LPWSTR</c> holding a CoTaskMem string is
    /// exactly what <c>PropVariantClear</c> knows how to free.
    /// </summary>
    private static void Set(IPropertyStore store, PropertyKey key, string value)
    {
        var variant = new PropVariant
        {
            Type = VtLpwstr,
            Value = Marshal.StringToCoTaskMemUni(value),
        };

        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>
    /// Only ever holds a string here, but it has to be the full 24-byte shape the shell writes
    /// into, so the payload fields are present and never read.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Padding;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);

        void GetAt(uint index, out PropertyKey key);

        void GetValue(ref PropertyKey key, out PropVariant value);

        void SetValue(ref PropertyKey key, ref PropVariant value);

        void Commit();
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(IntPtr window, in Guid iid, out IntPtr store);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant variant);
}
