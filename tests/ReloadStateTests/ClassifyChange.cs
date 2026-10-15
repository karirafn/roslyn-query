using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class ClassifyChange
{
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
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete);

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
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete);

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
            sut.CompleteInitialLoad(solution, [csprojPath], documentPaths, LoadHealth.Complete);

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
}
