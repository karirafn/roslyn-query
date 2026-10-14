using System.Collections.Concurrent;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

public static class SolutionLoader
{
    /// <summary>
    /// Materializes the Roslyn compilation for every project in <paramref name="solution"/>,
    /// so that the first real query does not pay the cold <c>GetCompilationAsync</c> cost.
    /// </summary>
    public static async Task WarmUpAsync(Solution solution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        foreach (Project project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await project.GetCompilationAsync(cancellationToken);
        }
    }

    public static async Task<LoadHealth> LoadWithHealthAsync(
        MSBuildWorkspace workspace,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(solutionPath);

        ConcurrentBag<string> failures = [];
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                failures.Add(e.Diagnostic.Message);
            }
        });

        await LoadAsync(workspace, solutionPath, cancellationToken);

        return BuildHealth(failures);
    }

    internal static LoadHealth BuildHealth(IReadOnlyCollection<string> failures)
    {
        return failures.Count == 0 ? LoadHealth.Complete : new LoadHealth([.. failures]);
    }

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
