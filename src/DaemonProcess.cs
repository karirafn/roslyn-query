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

        string content =
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + "\n"
            + Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)
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
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.ProcessPath,
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        if (IsDotnetHost(startInfo.FileName))
        {
            startInfo.ArgumentList.Add(typeof(DaemonProcess).Assembly.Location);
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
        };

        int? pid = ReadPidFile(solutionPath);
        if (pid.HasValue)
        {
            try
            {
                using Process existing = Process.GetProcessById(pid.Value);
                if (IsDaemonProcess(existing))
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

    public static void StopDaemon(string solutionPath)
    {
        string pidFilePath = PipeProtocol.DerivePidFilePath(solutionPath);
        if (!File.Exists(pidFilePath))
        {
            return;
        }
        StopAndCleanupPidFile(pidFilePath);
    }

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

    private static void StopAndCleanupPidFile(string pidFilePath)
    {
        try
        {
            string[] lines = ReadPidLines(pidFilePath);

            if (lines.Length == 0 || !int.TryParse(lines[0], CultureInfo.InvariantCulture, out int pid))
            {
                File.Delete(pidFilePath);
                return;
            }

            try
            {
                using Process process = Process.GetProcessById(pid);
                if (!IsDaemonProcess(process))
                {
                    return;
                }
                process.Kill();
                process.WaitForExit();
            }
            catch (ArgumentException)
            {
                // Process already exited — stale PID file
            }
            catch (InvalidOperationException)
            {
                // Process exited between GetProcessById and Kill
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Kill() was denied — leave PID file intact so future stop attempts can retry
                return;
            }

            File.Delete(pidFilePath);
        }
        catch (IOException)
        {
            // File disappeared or is locked — skip and continue to next PID file
        }
    }

    internal static bool IsDaemonProcess(Process process)
    {
        try
        {
            string expectedExe = Path.GetFileNameWithoutExtension(
                Environment.ProcessPath ?? string.Empty);
            string actualExe = Path.GetFileNameWithoutExtension(
                process.MainModule?.FileName ?? string.Empty);
            return string.Equals(expectedExe, actualExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // Can't read MainModule (e.g., access denied for another user's process)
            return false;
        }
    }
}
