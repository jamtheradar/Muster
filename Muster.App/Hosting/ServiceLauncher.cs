using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Muster.Core.Config;
using Muster.Core.Hosting;

namespace Muster.App.Hosting;

/// <summary>
/// The process behind one launched service: started when its session is, read until it says where
/// it is listening, and killed — with everything it started — when the session goes.
/// </summary>
/// <remarks>
/// <para>
/// The process is put in a job object that kills on close. <see cref="Process.Kill(bool)"/> covers
/// a tab being closed or the app quitting normally; the job covers the app dying, which would
/// otherwise leave a server holding a port with nothing to close it. npx in particular is a chain
/// (<c>cmd</c>, then npm's node, then the package's node), and the one listening is the last.
/// </para>
/// <para>
/// Output is drained for the life of the process, not just until the address turns up. A server
/// that keeps logging into a pipe nobody reads blocks once the pipe buffer fills, which looks like
/// the page hanging some minutes after it loaded fine.
/// </para>
/// <para>
/// Nothing the process writes is logged. Local servers print their access token as part of the
/// address, which is exactly the line this is looking for.
/// </para>
/// </remarks>
public sealed partial class ServiceLauncher : IDisposable
{
    private const int TailLines = 20;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private readonly SessionDescriptor _descriptor;
    private readonly ServiceLaunchConfig _launch;
    private readonly ILogger _log;
    private readonly TaskCompletionSource<Uri> _address = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Queue<string> _tail = new();
    private readonly Lock _tailLock = new();
    private Process? _process;
    private nint _job;
    private bool _disposed;

    public ServiceLauncher(SessionDescriptor descriptor, ILogger log)
    {
        _descriptor = descriptor;
        _launch = descriptor.Launch
            ?? throw new ArgumentException($"Session {descriptor.Id} has nothing to launch.", nameof(descriptor));
        _log = log;
    }

    /// <summary>
    /// Starts the process and waits for its address.
    /// </summary>
    /// <exception cref="LaunchFailedException">
    /// The command could not be found or started, exited first, or said nothing usable in time.
    /// The message is written for the tab, not the log.
    /// </exception>
    /// <exception cref="OperationCanceledException">The session went away while waiting.</exception>
    public async Task<Uri> StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pattern = LaunchAddress.Compile(_launch.UrlPattern);
        var executable = Resolve(_launch.Command)
            ?? throw new LaunchFailedException($"'{_launch.Command}' was not found on PATH.", []);

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _launch.WorkingDirectory ?? Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in _launch.Args)
        {
            start.ArgumentList.Add(arg);
        }

        // Colour codes land in the middle of the line the pattern is looking for. Most CLIs honour
        // one or other of these when they are not writing to a terminal anyway, but not all.
        start.Environment["NO_COLOR"] = "1";
        start.Environment["FORCE_COLOR"] = "0";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(pattern, e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(pattern, e.Data);
        process.Exited += OnExited;

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new LaunchFailedException($"'{executable}' could not be started: {ex.Message}", []);
        }

        _process = process;
        AttachToJob(process);

        // Nothing is ever written to it. Closed so a tool that waits on stdin sees EOF rather than
        // waiting forever for a keypress that cannot come.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _log.LogInformation(
            "Started {Command} for {Session}, pid {Pid}, waiting up to {Timeout}s for its address",
            Path.GetFileName(executable),
            _descriptor.Id,
            process.Id,
            _launch.TimeoutSeconds);

        try
        {
            var address = await _address.Task
                .WaitAsync(TimeSpan.FromSeconds(_launch.TimeoutSeconds), _stopping.Token)
                .ConfigureAwait(true);

            _log.LogInformation("{Session} is listening at {Address}", _descriptor.Id, LaunchAddress.Redacted(address));
            return address;
        }
        catch (TimeoutException)
        {
            throw new LaunchFailedException(
                $"Nothing matching the url pattern appeared within {_launch.TimeoutSeconds} seconds.",
                Tail());
        }
    }

    private void OnLine(Regex pattern, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_tailLock)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailLines)
            {
                _tail.Dequeue();
            }
        }

        if (!_address.Task.IsCompleted && LaunchAddress.TryRead(pattern, line, _descriptor.Home, out var address))
        {
            _address.TrySetResult(address!);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        int? code = null;
        try
        {
            // The parameterless overload waits for the redirected streams to reach EOF, so the last
            // lines are in the tail before it is read below.
            process.WaitForExit();
            code = process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Disposed underneath us by a session closing. Nothing left to report to.
        }

        if (_address.TrySetException(new LaunchFailedException($"The process exited with code {code} before reporting an address.", Tail())))
        {
            return;
        }

        if (!_disposed)
        {
            // After a successful start this is the server going away under a live tab. The tab will
            // show a connection error on its next request, and this is the line that explains it.
            _log.LogWarning("Process for {Session} exited with code {Code}", _descriptor.Id, code);
        }
    }

    private IReadOnlyList<string> Tail()
    {
        lock (_tailLock)
        {
            return [.. _tail];
        }
    }

    /// <summary>
    /// The command as a shell would find it. <see cref="ProcessStartInfo"/> with shell execution
    /// off only tries <c>.exe</c>, and the commands this exists for — npx, npm, pnpm — are
    /// <c>.cmd</c> files on Windows.
    /// </summary>
    private static string? Resolve(string command)
    {
        if (Path.IsPathFullyQualified(command))
        {
            return File.Exists(command) ? command : null;
        }

        var extensions = Path.HasExtension(command)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), command + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters no path can hold. Skipped, as the shell does.
                }
            }
        }

        return null;
    }

    private void AttachToJob(Process process)
    {
        try
        {
            var job = CreateJobObjectW(0, null);
            if (job == 0)
            {
                throw new System.ComponentModel.Win32Exception();
            }

            var limits = new JobObjectExtendedLimit { LimitFlags = JobObjectLimitKillOnJobClose };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimit>())
                || !AssignProcessToJobObject(job, process.Handle))
            {
                var error = new System.ComponentModel.Win32Exception();
                CloseHandle(job);
                throw error;
            }

            _job = job;
        }
        catch (Exception ex)
        {
            // Best effort. Without it a crash can orphan the server, but a normal close still kills
            // the tree, so this is a degraded launch rather than a failed one.
            _log.LogWarning(ex, "Could not put the process for {Session} in a job; it may outlive a crash", _descriptor.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();

        if (_process is { } process)
        {
            process.Exited -= OnExited;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _log.LogInformation("Stopped the process for {Session}", _descriptor.Id);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.LogDebug(ex, "Process for {Session} was already gone", _descriptor.Id);
            }

            process.Dispose();
        }

        // Closing the last handle to a kill-on-close job ends anything the tree kill missed.
        if (_job != 0)
        {
            CloseHandle(_job);
            _job = 0;
        }

        _stopping.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimit info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
