using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class ClassifyChange
{
    private sealed class TrackingDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string name, string content = "class Foo {}")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task WhenTrackedFileIsNewer_ReturnsFullAsync()
    {
        // Arrange
        string dir = CreateTempDir();

        try
        {
            string csprojPath = WriteFile(dir, "Alpha.csproj", "<Project />");

            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, oldTime);

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;
            ReloadState sut = new(solution, [csprojPath]);

            // Update the tracked file after construction so it appears newer
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, newTime);

            // Act
            StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

            // Assert
            result.ShouldBe(StalenessVerdict.Full);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WhenTrackedFilesAreUnchangedAndNoDocumentChanges_ReturnsNoneAsync()
    {
        // Arrange
        string dir = CreateTempDir();

        try
        {
            string csprojPath = WriteFile(dir, "Alpha.csproj", "<Project />");

            DateTime fixedTime = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, fixedTime);

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;
            ReloadState sut = new(solution, [csprojPath]);

            // Act
            StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

            // Assert
            result.ShouldBe(StalenessVerdict.None);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WhenStructuralDirtyIsTrue_ReturnsFullAsync()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        // Act
        StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: true);

        // Assert
        result.ShouldBe(StalenessVerdict.Full);
    }

    [Fact]
    public async Task WhenDocumentDeletedAfterInitialLoad_ReturnsFullAsync()
    {
        // Arrange
        string dir = CreateTempDir();

        try
        {
            string docPath = WriteFile(dir, "Foo.cs");

            DateTime fixedTime = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, fixedTime);

            using AdhocWorkspace workspace = new();
            ProjectId projectId = ProjectId.CreateNewId();
            DocumentId documentId = DocumentId.CreateNewId(projectId);

            Solution solution = workspace
                .CurrentSolution
                .AddProject(projectId, "Alpha", "Alpha", LanguageNames.CSharp)
                .AddDocument(documentId, "Foo.cs", "class Foo {}", filePath: docPath);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            // Delete the tracked document — structural change
            File.Delete(docPath);

            // Act
            StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

            // Assert
            result.ShouldBe(StalenessVerdict.Full);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WhenDocumentEditedAfterInitialLoad_ReturnsIncrementalAsync()
    {
        // Arrange
        string dir = CreateTempDir();

        try
        {
            string docPath = WriteFile(dir, "Foo.cs");

            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, oldTime);

            using AdhocWorkspace workspace = new();
            ProjectId projectId = ProjectId.CreateNewId();
            DocumentId documentId = DocumentId.CreateNewId(projectId);

            Solution solution = workspace
                .CurrentSolution
                .AddProject(projectId, "Alpha", "Alpha", LanguageNames.CSharp)
                .AddDocument(documentId, "Foo.cs", "class Foo {}", filePath: docPath);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            // Advance mtime — document edited
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, newTime);

            // Act
            StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

            // Assert
            result.ShouldBe(StalenessVerdict.Incremental);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WhenTrackedFileNewerAndDocumentEdited_FullWinsAsync()
    {
        // Arrange
        string dir = CreateTempDir();

        try
        {
            string csprojPath = WriteFile(dir, "Alpha.csproj", "<Project />");
            string docPath = WriteFile(dir, "Foo.cs");

            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, oldTime);
            File.SetLastWriteTimeUtc(docPath, oldTime);

            using AdhocWorkspace workspace = new();
            ProjectId projectId = ProjectId.CreateNewId();
            DocumentId documentId = DocumentId.CreateNewId(projectId);

            Solution solution = workspace
                .CurrentSolution
                .AddProject(projectId, "Alpha", "Alpha", LanguageNames.CSharp)
                .AddDocument(documentId, "Foo.cs", "class Foo {}", filePath: docPath);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, [csprojPath]);
            sut.CompleteInitialLoad(solution, [csprojPath], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            // Both tracked file and document are newer
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, newTime);
            File.SetLastWriteTimeUtc(docPath, newTime);

            // Act
            StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

            // Assert
            result.ShouldBe(StalenessVerdict.Full);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WhenAbortReloadCalled_NextClassifyReturnsFullEvenWithNoFileChangesAsync()
    {
        // Arrange — no files, so the result would normally be None
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Act
        StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

        // Assert — abort forces Full so the daemon retries the reload automatically
        result.ShouldBe(StalenessVerdict.Full);
    }

    [Fact]
    public async Task WhenCompleteReloadFollowsAbort_DirtyFlagClearedAsync()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Retry: a successful CompleteReload must clear the dirty flag
        sut.TryBeginReload().ShouldBeTrue();
        sut.CompleteReload(
            solution,
            [],
            FrozenSet<string>.Empty,
            LoadHealth.Complete,
            new WorkspaceLease(new TrackingDisposable()));

        // Act
        StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

        // Assert — dirty flag cleared; no files changed so verdict is None
        result.ShouldBe(StalenessVerdict.None);
    }
}
