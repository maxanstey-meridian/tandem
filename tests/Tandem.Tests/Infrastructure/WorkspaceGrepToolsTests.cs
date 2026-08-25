using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class WorkspaceGrepToolsTests
{
    [Fact]
    public void Add_RegistersOnePaginatedGrepTool()
    {
        var options = new ChatOptions();

        WorkspaceGrepTools.Add(options, ".");

        var tool = options
            .Tools.Should()
            .ContainSingle()
            .Which.Should()
            .BeAssignableTo<AIFunction>()
            .Subject;
        tool.Name.Should().Be("file_access_grep");
        tool.JsonSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(
                "regexPattern",
                "directory",
                "globPattern",
                "recursive",
                "offset",
                "limit"
            );
    }

    [Fact]
    public async Task SearchAsync_ReturnsDeterministicNormalizedRecursiveResults()
    {
        var root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "z"));
            Directory.CreateDirectory(Path.Combine(root, "a"));
            await File.WriteAllTextAsync(Path.Combine(root, "z", "two.txt"), "none\nMATCH two\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "a", "one.txt"),
                "match one\nMATCH again\n"
            );

            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                0,
                65536
            );

            page.Content.Should()
                .Be("a/one.txt:1:match one\na/one.txt:2:MATCH again\nz/two.txt:2:MATCH two\n");
            page.HasMore.Should().BeFalse();
            page.TotalLength.Should().Be(page.Content.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_PrunesExcludedDirectoriesAndRejectsBinaryAndGlobBeforeDecoding()
    {
        var root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "obj", "nested"));
            await File.WriteAllBytesAsync(
                Path.Combine(root, "obj", "nested", "bad.txt"),
                [0xff, 0xff]
            );
            await File.WriteAllBytesAsync(Path.Combine(root, "bad.dll"), [0xff, 0xff]);
            await File.WriteAllBytesAsync(Path.Combine(root, "wrong.cs"), [0xff, 0xff]);
            await File.WriteAllBytesAsync(Path.Combine(root, "binary.txt"), [0, 1, 2, 3, 4, 5]);
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "MATCH");
            var enumerated = new List<string>();
            var opened = new List<string>();
            var decoded = new List<string>();
            var diagnostics = new WorkspaceGrepTools.SearchDiagnostics(
                enumerated.Add,
                opened.Add,
                decoded.Add
            );

            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                "*.txt",
                true,
                0,
                65536,
                diagnostics: diagnostics
            );

            page.Content.Should().Be("keep.txt:1:MATCH\n");
            enumerated.Should().NotContain("obj").And.NotContain("obj/nested");
            opened.Should().NotContain(["bad.dll", "wrong.cs", "obj/nested/bad.txt"]);
            opened.Should().Contain("binary.txt");
            decoded.Should().NotContain("binary.txt");
            decoded.Should().ContainSingle().Which.Should().Be("keep.txt");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_RespectsNonRecursiveSearchAndEmptyResults()
    {
        var root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "match.txt"), "MATCH");

            var page = await WorkspaceGrepTools.SearchAsync(root, "", "MATCH", null, false, 0, 64);

            page.Should().Be(new TextPage("", 0, 0, 0, false, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_PagesWithoutOverlapAndPreservesSurrogates()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "emoji.txt"),
                "MATCH 😀 value\nMATCH second"
            );
            var complete = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                null,
                true,
                0,
                65536
            );
            var pieces = new List<string>();
            var offset = 0;
            do
            {
                var page = await WorkspaceGrepTools.SearchAsync(
                    root,
                    "",
                    "MATCH",
                    null,
                    true,
                    offset,
                    10
                );
                pieces.Add(page.Content);
                if (!page.HasMore)
                {
                    break;
                }

                offset = page.NextOffset!.Value;
            } while (true);

            string.Concat(pieces).Should().Be(complete.Content);
            (
                await WorkspaceGrepTools.SearchAsync(
                    root,
                    "",
                    "MATCH",
                    null,
                    true,
                    complete.TotalLength,
                    10
                )
            )
                .Should()
                .Be(new TextPage("", complete.TotalLength, 0, complete.TotalLength, false, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_RejectsInvalidInputsAndHonorsCancellation()
    {
        var root = CreateDirectory();
        try
        {
            var invalidRegex = () =>
                WorkspaceGrepTools.SearchAsync(root, "", "[", null, true, 0, 10);
            await invalidRegex.Should().ThrowAsync<ArgumentException>();
            var invalidOffset = () =>
                WorkspaceGrepTools.SearchAsync(root, "", "x", null, true, 1, 10);
            await invalidOffset.Should().ThrowAsync<ArgumentOutOfRangeException>();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var canceled = () =>
                WorkspaceGrepTools.SearchAsync(
                    root,
                    "",
                    "x",
                    null,
                    true,
                    0,
                    10,
                    cancellation.Token
                );
            await canceled.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_DoubleStarGlobMatchesRootAndNestedPaths()
    {
        var root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "root.cs"), "MATCH");
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "child.cs"), "MATCH");

            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                "**/*.cs",
                true,
                0,
                65536
            );

            page.Content.Should().Be("nested/child.cs:1:MATCH\nroot.cs:1:MATCH\n");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_RegexExecutionIsBounded()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "catastrophic.txt"),
                new string('a', 100_000) + "!"
            );

            var search = () =>
                WorkspaceGrepTools.SearchAsync(root, "", "^(a+)+$", null, true, 0, 10);

            await search
                .Should()
                .ThrowAsync<System.Text.RegularExpressions.RegexMatchTimeoutException>();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_RejectsEscapesMissingDirectoriesAndReparsePoints()
    {
        var parent = CreateDirectory();
        var root = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(root);
        try
        {
            var escape = () => WorkspaceGrepTools.SearchAsync(root, "..", "x", null, true, 0, 10);
            await escape.Should().ThrowAsync<UnauthorizedAccessException>();
            var missing = () =>
                WorkspaceGrepTools.SearchAsync(root, "missing", "x", null, true, 0, 10);
            await missing.Should().ThrowAsync<DirectoryNotFoundException>();

            var outside = Path.Combine(parent, "outside");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, outside);
            var linked = () => WorkspaceGrepTools.SearchAsync(root, "link", "x", null, true, 0, 10);
            await linked.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task SearchAsync_ReportsInaccessibleDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = CreateDirectory();
        var blocked = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blocked);
        File.SetUnixFileMode(blocked, UnixFileMode.None);
        try
        {
            var search = () =>
                WorkspaceGrepTools.SearchAsync(root, "blocked", "x", null, true, 0, 10);

            await search.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetUnixFileMode(
                blocked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_BoundsReturnedMemoryForLargeFilesAndMatchSets()
    {
        var root = CreateDirectory();
        try
        {
            var line = "MATCH " + new string('x', 20_000);
            await File.WriteAllLinesAsync(
                Path.Combine(root, "large.txt"),
                Enumerable.Repeat(line, 2_000)
            );

            var page = await WorkspaceGrepTools.SearchAsync(root, "", "MATCH", null, true, 0, 128);

            page.Length.Should().BeLessThanOrEqualTo(128);
            page.Content.Length.Should().Be(page.Length);
            page.TotalLength.Should().BeGreaterThan(40_000_000);
            page.HasMore.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tandem-grep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
