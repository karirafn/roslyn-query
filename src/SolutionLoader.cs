using Microsoft.CodeAnalysis;
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

    public static async Task LoadProjectsAsync(
        Workspace workspace,
        IReadOnlyList<string> projectPaths,
        Func<string, CancellationToken, Task> openProject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentNullException.ThrowIfNull(openProject);
        foreach (string projectPath in projectPaths)
        {
            bool alreadyLoaded = workspace.CurrentSolution.Projects
                .Any(p => string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));
            if (!alreadyLoaded)
            {
                await openProject(projectPath, cancellationToken);
            }
        }
    }

    private static async Task LoadSlnxAsync(
        MSBuildWorkspace workspace,
        string slnxPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectPaths = SlnxReader.ReadProjectPaths(slnxPath);
        await LoadProjectsAsync(
            workspace,
            projectPaths,
            (path, ct) => workspace.OpenProjectAsync(path, cancellationToken: ct),
            cancellationToken);
    }
}
