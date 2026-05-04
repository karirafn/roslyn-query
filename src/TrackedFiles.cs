using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public static class TrackedFiles
{
    private static readonly string[] PropsFileNames =
        ["Directory.Packages.props", "Directory.Build.props"];

    private static readonly DateTime MissingFileSentinel = DateTime.FromFileTimeUtc(0);

    public static IReadOnlyList<string> CollectPaths(
        Solution solution,
        string solutionDirectory,
        string solutionFilePath)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrEmpty(solutionFilePath);
        ArgumentException.ThrowIfNullOrEmpty(solutionDirectory);

        List<string> paths = [solutionFilePath];

        paths.AddRange(solution.Projects
            .Where(p => p.FilePath is not null)
            .Where(p => File.Exists(p.FilePath))
            .Select(p => p.FilePath!));

        paths.AddRange(DiscoverPropsFiles(solutionDirectory));

        return paths;
    }

    public static DateTime ComputeMaxWriteTime(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        DateTime max = DateTime.MinValue;

        foreach (string path in paths)
        {
            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime == MissingFileSentinel)
            {
                writeTime = DateTime.MaxValue;
            }

            if (writeTime > max)
            {
                max = writeTime;
            }
        }

        return max;
    }

    private static IEnumerable<string> DiscoverPropsFiles(string startDirectory)
    {
        string? current = startDirectory;

        while (current is not null)
        {
            foreach (string fileName in PropsFileNames)
            {
                string candidate = Path.Combine(current, fileName);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }

            string? parent = Path.GetDirectoryName(current);
            current = parent != current ? parent : null;
        }
    }
}
