using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RoslynQuery;

public static class DaemonProcess
{
    public static void WritePidFile(string solutionPath)
    {
        string path = PipeProtocol.DerivePidFilePath(solutionPath);
        File.WriteAllText(path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

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

        string content = File.ReadAllText(path);

        if (int.TryParse(content, CultureInfo.InvariantCulture, out int pid))
        {
            return pid;
        }

        return null;
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
            FileName = "roslyn-query",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("--daemon");
        startInfo.ArgumentList.Add(solutionPath);

        return startInfo;
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
        string tempPath = Path.GetTempPath();
        IEnumerable<string> pidFiles = Directory.EnumerateFiles(tempPath, $"{PipeProtocol.Prefix}*.pid");

        foreach (string pidFilePath in pidFiles)
        {
            StopAndCleanupPidFile(pidFilePath);
        }
    }

    private static void StopAndCleanupPidFile(string pidFilePath)
    {
        try
        {
            string content = File.ReadAllText(pidFilePath);

            if (!int.TryParse(content, CultureInfo.InvariantCulture, out int pid))
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
