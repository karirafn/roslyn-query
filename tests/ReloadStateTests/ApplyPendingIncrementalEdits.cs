using System.Collections.Frozen;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class ApplyPendingIncrementalEdits : IDisposable
{
    private sealed class TrackingDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private readonly string _dir;

    public ApplyPendingIncrementalEdits()
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
            sut.CompleteInitialLoad(
                solution,
                [],
                documentPaths,
                LoadHealth.Complete,
                new WorkspaceLease(new TrackingDisposable()));

            // Update the file on disk and advance mtime
            await File.WriteAllTextAsync(docPath, UpdatedContent);
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, newTime);

            // Act — no list passed; method detects and applies internally
            await sut.ApplyPendingIncrementalEditsAsync();

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
    public async Task WhenNoDocumentChanged_SolutionUnchangedAsync()
    {
        // Arrange
        const string InitialContent = "class Foo {}";

        string docPath = WriteFile("Foo.cs", InitialContent);
        (AdhocWorkspace workspace, Solution solution, DocumentId documentId) =
            BuildSolution(docPath, InitialContent);

        using (workspace)
        {
            DateTime fixedTime = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, fixedTime);

            FrozenSet<string> documentPaths = [docPath];
            ReloadState sut = new(solution, []);
            sut.CompleteInitialLoad(
                solution,
                [],
                documentPaths,
                LoadHealth.Complete,
                new WorkspaceLease(new TrackingDisposable()));

            // Act — no changes on disk
            await sut.ApplyPendingIncrementalEditsAsync();

            // Assert — solution and write-times unchanged
            (Solution snapshotSolution, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();
            snapshotLease.Release();
            Document? doc = snapshotSolution.GetDocument(documentId);
            doc.ShouldNotBeNull();
            SourceText text = await doc.GetTextAsync();
            text.ToString().ShouldBe(InitialContent);
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
            sut.CompleteInitialLoad(
                solution,
                [],
                documentPaths,
                LoadHealth.Complete,
                new WorkspaceLease(new TrackingDisposable()));

            // Write updated content and advance mtime
            await File.WriteAllTextAsync(docPath, "class Bar {}");
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(docPath, newTime);

            await sut.ApplyPendingIncrementalEditsAsync();

            // Act — classify again without any further file changes
            StalenessVerdict verdict = await sut.ClassifyChangeAsync(structuralDirty: false);

            // Assert — mtime map absorbed the edit so no further change is detected
            verdict.ShouldBe(StalenessVerdict.None);
        }
    }
}
