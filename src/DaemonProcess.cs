using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RoslynQuery;

public static class DaemonProcess
{
    private const string DotnetHostName = "dotnet";

    public static void WritePidFile(string solutionPath)
    {
        PipeProtocol.PrepareStateDirectory();
        string path = PipeProtocol.DerivePidFilePath(solutionPath);

        // Refuse to write through a symlinked PID file — a symlink could redirect writes
        // to an attacker-controlled location, undermining the 0600 mode set below.
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException(
                $"PID file path '{path}' is a symbolic link. A symlinked PID file is refused.");
        }

        using Process self = Process.GetCurrentProcess();
        string content =
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + "\n"
            + self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)
            + "\n";

        FileStreamOptions options = OperatingSystem.IsWindows()
            ? new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write }
            : new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };

        using (FileStream stream = new(path, options))
        using (StreamWriter writer = new(stream))
        {
            writer.Write(content);
        }

        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = new(path, AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    public static int? ReadPidFile(string solutionPath)
    {
        string path = PipeProtocol.DerivePidFilePath(solutionPath);

        if (!File.Exists(path))
        {
            return null;
        }

        string[] lines = ReadPidLines(path);

        if (lines.Length == 0 || !int.TryParse(lines[0], CultureInfo.InvariantCulture, out int pid))
        {
            return null;
        }

        return pid;
    }

    /// <summary>
    /// Reads the PID file and returns both the process ID and optional start-time ticks.
    /// Returns null when the file is missing or line 1 is not a valid integer.
    /// <c>StartTimeTicks</c> is null for legacy single-line files or when line 2 is not a valid long.
    /// </summary>
    internal static (int Pid, long? StartTimeTicks)? ReadPidRecord(string solutionPath)
    {
        string path = PipeProtocol.DerivePidFilePath(solutionPath);

        if (!File.Exists(path))
        {
            return null;
        }

        return ReadPidRecordFromPath(path);
    }

    /// <summary>
    /// Parses a PID record from a file path that is already known to exist.
    /// Returns null when line 1 is not a valid integer.
    /// <c>StartTimeTicks</c> is null for legacy single-line files or when line 2 is not a valid long.
    /// </summary>
    private static (int Pid, long? StartTimeTicks)? ReadPidRecordFromPath(string path)
    {
        string[] lines = ReadPidLines(path);

        if (lines.Length == 0 || !int.TryParse(lines[0], CultureInfo.InvariantCulture, out int pid))
        {
            return null;
        }

        long? startTimeTicks = null;
        if (lines.Length >= 2 && long.TryParse(lines[1], CultureInfo.InvariantCulture, out long ticks))
        {
            startTimeTicks = ticks;
        }

        return (pid, startTimeTicks);
    }

    private static string[] ReadPidLines(string path)
    {
        string content = File.ReadAllText(path);
        return content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    public static void CleanupPidFile(string solutionPath)
    {
        string path = PipeProtocol.DerivePidFilePath(solutionPath);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static bool IsProcessRunning(int pid)
    {
        try
        {
            Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static ProcessStartInfo BuildStartInfo(string solutionPath)
    {
        string? processPath = Environment.ProcessPath;
        string assemblyLocation = typeof(DaemonProcess).Assembly.Location;
        return BuildStartInfoCore(solutionPath, processPath, assemblyLocation);
    }

    /// <summary>
    /// Core implementation — accepts injected paths so the dotnet-host branch and the
    /// null-processPath guard are reachable from tests without relying on the runtime environment.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfoCore(
        string solutionPath,
        string? processPath,
        string assemblyLocation)
    {
        if (processPath is null)
        {
            throw new InvalidOperationException(
                "Cannot determine the current executable path (Environment.ProcessPath is null); " +
                "unable to spawn the daemon.");
        }

        // On Windows, UseShellExecute=true spawns the daemon through ShellExecute, which does not
        // inherit the caller's stdio handles, so a piped caller reaches EOF when the client exits
        // rather than when the daemon dies. On Unix, redirect the three std streams and close the
        // parent's ends immediately after start (see StartDaemon) for the same effect.
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }
            : new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

        if (IsDotnetHost(processPath))
        {
            startInfo.ArgumentList.Add(assemblyLocation);
        }

        startInfo.ArgumentList.Add("--daemon");
        startInfo.ArgumentList.Add(solutionPath);

        return startInfo;
    }

    private static bool IsDotnetHost(string? processPath)
    {
        return string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            DotnetHostName,
            StringComparison.OrdinalIgnoreCase);
    }

    public static void StartDaemon(string solutionPath, Action? spawnDaemon = null)
    {
        spawnDaemon ??= () =>
        {
            ProcessStartInfo startInfo = BuildStartInfo(solutionPath);
            using Process process = new() { StartInfo = startInfo };
            process.Start();

            // On Unix, close the parent's ends of the redirected pipes immediately after starting
            // the daemon. This drops the parent's handle to the inherited pipe so the caller's
            // piped stdout reaches EOF when the client exits, not when the daemon exits.
            // On Windows, UseShellExecute=true prevents handle inheritance entirely.
            if (!OperatingSystem.IsWindows())
            {
                process.StandardInput.Close();
                process.StandardOutput.Close();
                process.StandardError.Close();
            }
        };

        (int Pid, long? StartTimeTicks)? record = ReadPidRecord(solutionPath);
        if (record.HasValue)
        {
            try
            {
                using Process existing = Process.GetProcessById(record.Value.Pid);

                // Recognise the running daemon only when the record carries a start time AND
                // the live process's start time matches exactly.  A legacy single-line file
                // (no start time) or a reused PID with a different start time both fail
                // closed: clean up and spawn rather than risk skipping a real spawn or killing
                // an unrelated process.
                if (record.Value.StartTimeTicks is long expectedTicks
                    && IsRecordedDaemon(existing, expectedTicks))
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                // Process already exited — stale PID file, clean up and spawn
            }

            try
            {
                CleanupPidFile(solutionPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // PID file was already deleted concurrently — that is fine, we were cleaning it up anyway
            }
        }

        spawnDaemon();
    }

    /// <summary>
    /// Stops the daemon for the given solution path. Attempts graceful shutdown over the
    /// authenticated pipe first; falls back to a start-time-verified kill when the pipe is
    /// unreachable. Does nothing when no PID file exists.
    /// </summary>
    public static async Task StopDaemon(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        string pidFilePath = PipeProtocol.DerivePidFilePath(solutionPath);
        if (!File.Exists(pidFilePath))
        {
            return;
        }

        // Pipe-first: ask the daemon to shut itself down gracefully.
        bool acked = await DaemonClient.TryRequestShutdownAsync(solutionPath, cancellationToken)
            .ConfigureAwait(false);

        if (acked)
        {
            // Daemon acknowledged — its own finally block deletes the PID file.
            // Clean up in case the file lingers due to a race between the ack and the finally.
            try
            {
                if (File.Exists(pidFilePath))
                {
                    File.Delete(pidFilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Already gone or inaccessible — that is fine
            }

            return;
        }

        // Fallback: no daemon reachable over the pipe — verified kill.
        StopAndCleanupPidFile(pidFilePath);
    }

    /// <summary>
    /// Stops all daemons by enumerating PID files in the state directory.
    /// Uses identity-verified kill (start-time check); graceful pipe shutdown is not
    /// available here because the solution path cannot be derived from the PID file alone.
    /// </summary>
    public static void StopAllDaemons()
    {
        string pidDirectory = PipeProtocol.GetStateDirectory();
        if (!Directory.Exists(pidDirectory))
        {
            return;
        }

        IEnumerable<string> pidFiles = Directory.EnumerateFiles(pidDirectory, $"{PipeProtocol.Prefix}*.pid");

        foreach (string pidFilePath in pidFiles)
        {
            StopAndCleanupPidFile(pidFilePath);
        }
    }

    /// <summary>
    /// Reads a PID file, kills the process only when it is alive AND the start-time record
    /// matches (IsRecordedDaemon returns true), then deletes the file.
    /// <para>
    /// When line 2 (start-time) is absent (legacy file) or the start time mismatches, the
    /// process is NOT killed — fail closed — but the file IS deleted when the process is
    /// already dead (ArgumentException from GetProcessById).
    /// </para>
    /// </summary>
    private static void StopAndCleanupPidFile(string pidFilePath)
    {
        try
        {
            (int Pid, long? StartTimeTicks)? record = ReadPidRecordFromPath(pidFilePath);

            if (record is null)
            {
                File.Delete(pidFilePath);
                return;
            }

            int pid = record.Value.Pid;
            long? startTimeTicks = record.Value.StartTimeTicks;

            try
            {
                using Process process = Process.GetProcessById(pid);

                // Kill only when we can verify start-time identity.
                // No start-time (legacy file) or mismatch → not our daemon → leave intact.
                if (startTimeTicks is long expectedTicks && IsRecordedDaemon(process, expectedTicks))
                {
                    process.Kill();
                    process.WaitForExit();
                    File.Delete(pidFilePath);
                }

                // else: unverifiable or mismatched — leave the file intact (fail closed)
            }
            catch (ArgumentException)
            {
                // Process already exited — the file is a genuine stale record; delete it.
                File.Delete(pidFilePath);
            }
            catch (InvalidOperationException)
            {
                // Process exited between GetProcessById and Kill — treat as stale; delete.
                File.Delete(pidFilePath);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Kill() was denied — leave PID file intact so future stop attempts can retry
            }
        }
        catch (IOException)
        {
            // File disappeared or is locked — skip and continue to next PID file
        }
    }

    /// <summary>
    /// Returns true when <paramref name="process"/> has a start time (in UTC ticks) that
    /// exactly matches <paramref name="expectedStartTimeTicks"/>.  Returns false — fail closed —
    /// when the start time cannot be read (process exited, access denied, or not supported).
    /// </summary>
    internal static bool IsRecordedDaemon(Process process, long expectedStartTimeTicks)
    {
        try
        {
            long actualTicks = process.StartTime.ToUniversalTime().Ticks;
            return actualTicks == expectedStartTimeTicks;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // Cannot verify start time — treat as not our daemon (fail closed).
            return false;
        }
    }
}
