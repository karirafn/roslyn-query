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

        IEnumerable<string> projectFilePaths = solution.Projects
            .Where(p => p.FilePath is not null)
            .Where(p => File.Exists(p.FilePath))
            .Select(p => p.FilePath!);

        foreach (string projectFilePath in projectFilePaths)
        {
            paths.Add(projectFilePath);

            string lockFilePath = Path.Combine(
                Path.GetDirectoryName(projectFilePath)!,
                "packages.lock.json");

            if (File.Exists(lockFilePath))
            {
                paths.Add(lockFilePath);
            }
        }

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

    private const int MaxAncestorDepth = 20;

    private static IEnumerable<string> DiscoverPropsFiles(string startDirectory)
    {
        string? current = startDirectory;
        int depth = 0;

        while (current is not null && depth <= MaxAncestorDepth)
        {
            foreach (string fileName in PropsFileNames)
            {
                string candidate = Path.Combine(current, fileName);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }

            bool isGitRoot = Directory.Exists(Path.Combine(current, ".git"));
            if (isGitRoot)
            {
                yield break;
            }

            string? parent = Path.GetDirectoryName(current);
            current = parent != current ? parent : null;
            depth++;
        }
    }
}
