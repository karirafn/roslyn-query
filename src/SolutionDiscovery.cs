namespace RoslynQuery;

public static class SolutionDiscovery
{
    public static string? Discover(string startDir, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(startDir);
        ArgumentNullException.ThrowIfNull(stderr);
        string? dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            string[] slns =
            [
                .. Directory.GetFiles(dir, "*.sln"),
                .. Directory.GetFiles(dir, "*.slnx"),
            ];
            if (slns.Length == 1)
            {
                return slns[0];
            }
            if (slns.Length > 1)
            {
                stderr.WriteLine(
                    $"Multiple solution files in {dir} — specify one explicitly.");
                return null;
            }
            dir = Path.GetDirectoryName(dir);
        }
        stderr.WriteLine("No .sln or .slnx file found in current or parent directories.");
        return null;
    }
}
