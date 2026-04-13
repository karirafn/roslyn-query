using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

public static class SolutionLoader
{
    public static async Task LoadAsync(
        MSBuildWorkspace workspace,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(solutionPath);
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            await LoadSlnxAsync(workspace, solutionPath, cancellationToken);
            return;
        }

        await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
    }

    private static async Task LoadSlnxAsync(
        MSBuildWorkspace workspace,
        string slnxPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectPaths = SlnxReader.ReadProjectPaths(slnxPath);
        foreach (string projectPath in projectPaths)
        {
            await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        }
    }
}
