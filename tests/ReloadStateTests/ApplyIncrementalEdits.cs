using System.Collections.Frozen;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class ApplyIncrementalEdits : IDisposable
{
    private sealed class TrackingDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private readonly string _dir;

    public ApplyIncrementalEdits()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content = "class Foo {}")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static (AdhocWorkspace Workspace, Solution Solution, DocumentId DocumentId)
        BuildSolution(string docPath, string initialContent = "class Foo {}")
    {
        AdhocWorkspace workspace = new();
        ProjectId projectId = ProjectId.CreateNewId();
        DocumentId documentId = DocumentId.CreateNewId(projectId);

        Solution solution = workspace
            .CurrentSolution
            .AddProject(projectId, "Alpha", "Alpha", LanguageNames.CSharp)
            .AddDocument(documentId, Path.GetFileName(docPath), initialContent, filePath: docPath);

        return (workspace, solution, documentId);
    }

    [Fact]
    public async Task WhenDocumentEdited_SnapshotReflectsNewTextAsync()
    {
        // Arrange
        const string InitialContent = "class Foo {}";
        const string UpdatedContent = "class Bar {}";

        string docPath = WriteFile("Foo.cs", InitialContent);
        (AdhocWorkspace workspace, Solution solution, DocumentId documentId) =
            BuildSolution(docPath, InitialContent);

        using (workspace)
        {
            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, oldTime);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            // Update the file on disk
            await File.WriteAllTextAsync(docPath, UpdatedContent);
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, newTime);

            // Act
            sut.TryApplyIncrementalEdits([(docPath, newTime)]);

            // Assert
            (Solution snapshotSolution, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();
            snapshotLease.Release();
            Document? doc = snapshotSolution.GetDocument(documentId);
            doc.ShouldNotBeNull();
            SourceText text = await doc.GetTextAsync();
            text.ToString().ShouldBe(UpdatedContent);
        }
    }

    [Fact]
    public async Task WhenDocumentLinkedIntoMultipleProjects_AllLinkedDocumentsUpdatedAsync()
    {
        // Arrange
        const string InitialContent = "class Foo {}";
        const string UpdatedContent = "class Bar {}";

        string docPath = WriteFile("Foo.cs", InitialContent);

        AdhocWorkspace workspace = new();

        ProjectId projectId1 = ProjectId.CreateNewId();
        ProjectId projectId2 = ProjectId.CreateNewId();
        DocumentId documentId1 = DocumentId.CreateNewId(projectId1);
        DocumentId documentId2 = DocumentId.CreateNewId(projectId2);

        Solution solution = workspace
            .CurrentSolution
            .AddProject(projectId1, "Alpha", "Alpha", LanguageNames.CSharp)
            .AddProject(projectId2, "Beta", "Beta", LanguageNames.CSharp)
            .AddDocument(documentId1, "Foo.cs", InitialContent, filePath: docPath)
            .AddDocument(documentId2, "Foo.cs", InitialContent, filePath: docPath);

        using (workspace)
        {
            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, oldTime);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            // Update file on disk
            await File.WriteAllTextAsync(docPath, UpdatedContent);
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, newTime);

            // Act
            sut.TryApplyIncrementalEdits([(docPath, newTime)]);

            // Assert — both linked documents updated
            (Solution snapshotSolution, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();
            snapshotLease.Release();

            Document? doc1 = snapshotSolution.GetDocument(documentId1);
            doc1.ShouldNotBeNull();
            SourceText text1 = await doc1.GetTextAsync();
            text1.ToString().ShouldBe(UpdatedContent);

            Document? doc2 = snapshotSolution.GetDocument(documentId2);
            doc2.ShouldNotBeNull();
            SourceText text2 = await doc2.GetTextAsync();
            text2.ToString().ShouldBe(UpdatedContent);
        }
    }

    [Fact]
    public async Task WhenEditedPathHasNoMatchingDocumentId_SkipsWithoutThrowingAsync()
    {
        // Arrange
        const string InitialContent = "class Foo {}";

        string docPath = WriteFile("Foo.cs", InitialContent);
        string unknownPath = Path.Combine(_dir, "Unknown.cs");

        (AdhocWorkspace workspace, Solution solution, DocumentId documentId) =
            BuildSolution(docPath, InitialContent);

        using (workspace)
        {
            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, oldTime);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            // Act — unknown path has no matching DocumentId
            sut.TryApplyIncrementalEdits([(unknownPath, newTime)]);

            // Assert — solution unchanged, no throw
            (Solution snapshotSolution, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();
            snapshotLease.Release();
            Document? doc = snapshotSolution.GetDocument(documentId);
            doc.ShouldNotBeNull();
            SourceText text = await doc.GetTextAsync();
            text.ToString().ShouldBe(InitialContent);
        }
    }

    [Fact]
    public void WhenFileDeletedBeforeRead_SkipsWithoutThrowing()
    {
        // Arrange
        const string InitialContent = "class Foo {}";

        string docPath = WriteFile("Foo.cs", InitialContent);
        (AdhocWorkspace workspace, Solution solution, _) =
            BuildSolution(docPath, InitialContent);

        using (workspace)
        {
            // Arrange
            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, oldTime);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            // Delete the file to simulate a race — read will fail
            File.Delete(docPath);

            // Act — should skip without throwing
            sut.TryApplyIncrementalEdits([(docPath, newTime)]);

            // Assert — no exception; solution not updated
            (Solution snapshotSolution, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();
            snapshotLease.Release();
            snapshotSolution.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task WhenIncrementalEditApplied_SubsequentClassifyReturnsNoneAsync()
    {
        // Arrange
        const string InitialContent = "class Foo {}";

        string docPath = WriteFile("Foo.cs", InitialContent);
        (AdhocWorkspace workspace, Solution solution, _) = BuildSolution(docPath, InitialContent);

        using (workspace)
        {
            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, oldTime);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(solution, [], documentPaths, LoadHealth.Complete, new WorkspaceLease(new TrackingDisposable()));

            // Write updated content and advance mtime
            await File.WriteAllTextAsync(docPath, "class Bar {}");
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, newTime);

            sut.TryApplyIncrementalEdits([(docPath, newTime)]);

            // Act — classify again without any further file changes
            StalenessVerdict verdict = await sut.ClassifyChangeAsync(structuralDirty: false);

            // Assert — mtime map absorbed the edit so no further change is detected
            verdict.ShouldBe(StalenessVerdict.None);
        }
    }
}
