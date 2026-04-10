using System.Diagnostics;
using System.Globalization;

namespace RoslynQuery;

public static class DaemonProcess
{
    public static void WritePidFile(string solutionPath)
    {
        string path = PipeProtocol.DerivePidFilePath(solutionPath);
        File.WriteAllText(path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
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

    public static void StartDaemon(string solutionPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "roslyn-query",
            Arguments = $@"--daemon ""{solutionPath}""",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using Process process = new() { StartInfo = startInfo };
        process.Start();
    }

    public static void StopDaemon(string solutionPath)
    {
        int? pid = ReadPidFile(solutionPath);

        if (pid.HasValue && IsProcessRunning(pid.Value))
        {
            try
            {
                Process process = Process.GetProcessById(pid.Value);
                process.Kill();
            }
            catch (ArgumentException)
            {
                // Process exited between check and kill — safe to ignore
            }
        }

        CleanupPidFile(solutionPath);
    }
}
