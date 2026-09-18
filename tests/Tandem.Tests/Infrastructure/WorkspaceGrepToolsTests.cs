using FluentAssertions;

namespace Tandem.Tests.Infrastructure;

public sealed class WorkspaceGrepToolsTests
{
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
                null,
                500
            );

            page.Content.Should()
                .Be("a/one.txt:1:match one\na/one.txt:2:MATCH again\nz/two.txt:2:MATCH two\n");
            page.HasMore.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(
        "*.cs",
        true,
        "Rivet.Tests/CompilationHelper.cs:1:class CompilationHelper\nRivet.Tests/Nested/Child.cs:1:class CompilationHelper\n"
    )]
    [InlineData("*.cs", false, "Rivet.Tests/CompilationHelper.cs:1:class CompilationHelper\n")]
    [InlineData(
        "CompilationHelper.cs",
        true,
        "Rivet.Tests/CompilationHelper.cs:1:class CompilationHelper\n"
    )]
    [InlineData(
        "Rivet.Tests/*.cs",
        true,
        "Rivet.Tests/CompilationHelper.cs:1:class CompilationHelper\n"
    )]
    [InlineData(
        "**/*.cs",
        true,
        "Rivet.Tests/CompilationHelper.cs:1:class CompilationHelper\nRivet.Tests/Nested/Child.cs:1:class CompilationHelper\n"
    )]
    public async Task SearchAsync_DirectoryScopedGlobsFindOnlySelectedFiles(
        string glob,
        bool recursive,
        string expected
    )
    {
        var root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Rivet.Tests", "Nested"));
            foreach (
                var path in new[]
                {
                    "Outside.cs",
                    "Rivet.Tests/CompilationHelper.cs",
                    "Rivet.Tests/Excluded.txt",
                    "Rivet.Tests/Nested/Child.cs",
                }
            )
            {
                await File.WriteAllTextAsync(Path.Combine(root, path), "class CompilationHelper");
            }
            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                "Rivet.Tests",
                "class CompilationHelper",
                glob,
                recursive,
                null,
                500
            );
            page.Content.Should().Be(expected);
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
                WorkspaceGrepTools.SearchAsync(root, "", "^(a+)+$", null, true, null, 10);

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
            var escape = () =>
                WorkspaceGrepTools.SearchAsync(root, "..", "x", null, true, null, 10);
            await escape.Should().ThrowAsync<UnauthorizedAccessException>();
            var missing = () =>
                WorkspaceGrepTools.SearchAsync(root, "missing", "x", null, true, null, 10);
            await missing.Should().ThrowAsync<DirectoryNotFoundException>();

            var outside = Path.Combine(parent, "outside");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, outside);
            var linked = () =>
                WorkspaceGrepTools.SearchAsync(root, "link", "x", null, true, null, 10);
            await linked.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Theory]
    [InlineData(
        "Rivet.Tool/**/*.cs",
        "Rivet.Tool/Nested/Child.cs:1:MATCH\nRivet.Tool/Program.cs:1:MATCH\n",
        true
    )]
    [InlineData("rivet.tool/program.CS", "Rivet.Tool/Program.cs:1:MATCH\n", false)]
    [InlineData("Rivet.Tool\\Program.cs", "Rivet.Tool/Program.cs:1:MATCH\n", false)]
    public async Task SearchAsync_PathGlobsAvoidUnrelatedTraversal(
        string glob,
        string expected,
        bool nested
    )
    {
        var root = CreateDirectory();
        try
        {
            foreach (
                var path in new[]
                {
                    "Rivet.Tool/Program.cs",
                    "Rivet.Tool/Nested/Child.cs",
                    "Unrelated/Other.cs",
                    "Rivet.Tool/obj/Generated.cs",
                }
            )
            {
                var fullPath = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, "MATCH");
            }
            var visited = new List<string>();
            var opened = new List<string>();
            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                glob,
                true,
                null,
                500,
                diagnostics: new(visited.Add, opened.Add)
            );
            page.Content.Should().Be(expected);
            visited
                .Should()
                .BeEquivalentTo(
                    nested
                        ? new[] { ".", "Rivet.Tool", "Rivet.Tool/Nested" }
                        : new[] { ".", "Rivet.Tool" }
                );
            opened
                .Should()
                .NotContain(path => path.StartsWith("Unrelated/") || path.Contains("/obj/"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("Rivet.Tool", "Outside/File.cs", true)]
    [InlineData("", "Rivet.Tool/File.cs", false)]
    [InlineData("", "link/File.cs", true)]
    [InlineData("", "../Outside/File.cs", true)]
    public async Task SearchAsync_PathPrefixCannotBypassSearchBoundaries(
        string directory,
        string glob,
        bool recursive
    )
    {
        var root = CreateDirectory();
        try
        {
            foreach (var name in new[] { "Rivet.Tool", "Outside", "obj" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                await File.WriteAllTextAsync(Path.Combine(root, name, "File.cs"), "MATCH");
            }
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "Outside"));
            var opened = new List<string>();
            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                directory,
                "MATCH",
                glob,
                recursive,
                null,
                500,
                diagnostics: new(FileOpened: opened.Add)
            );
            page.Content.Should().BeEmpty();
            opened.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SearchAsync_SearchesSourceInsidePackages()
    {
        var root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "packages", "sdk"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "packages", "sdk", "Source.cs"),
                "MATCH"
            );
            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                "packages/**/*.cs",
                true,
                null,
                500
            );
            page.Content.Should().Be("packages/sdk/Source.cs:1:MATCH\n");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Pages_preserve_records_and_skip_completed_files()
    {
        var root = CreateDirectory();
        try
        {
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            {
                await File.WriteAllTextAsync(Path.Combine(root, name), "match😀\nmatch2\n");
            }

            var opened = new List<string>();
            var first = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                limit: 2,
                diagnostics: new(FileOpened: opened.Add)
            );
            opened.Should().Equal("a.txt", "b.txt");
            first.Matches.Select(m => m.Text).Should().Equal("match😀", "match2");
            opened.Clear();
            var second = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                first.NextCursor,
                2,
                diagnostics: new(FileOpened: opened.Add)
            );
            opened.Should().Equal("b.txt", "c.txt");
            var third = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                second.NextCursor,
                2
            );
            third.HasMore.Should().BeFalse();
            first
                .Matches.Concat(second.Matches)
                .Concat(third.Matches)
                .Select(m => (m.Path, m.Line))
                .Should()
                .OnlyHaveUniqueItems()
                .And.HaveCount(6);
            await File.AppendAllTextAsync(Path.Combine(root, "b.txt"), "changed");
            await FluentActions
                .Awaiting(() =>
                    WorkspaceGrepTools.SearchAsync(
                        root,
                        "",
                        "match",
                        null,
                        true,
                        first.NextCursor,
                        2
                    )
                )
                .Should()
                .ThrowAsync<ArgumentException>();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Exclusions_literal_case_and_skips_are_explicit()
    {
        var root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Library"));
            await File.WriteAllTextAsync(Path.Combine(root, "Library", "a.cs"), "A(b)\na(b)");
            await File.WriteAllBytesAsync(Path.Combine(root, "bad.txt"), [0xff, 0xff, 0x20]);
            var ordinary = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "A(b)",
                null,
                true,
                literal: true
            );
            ordinary.Matches.Should().BeEmpty();
            ordinary.SkippedCount.Should().Be(1);
            var included = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "A(b)",
                null,
                true,
                includeExcluded: true,
                literal: true,
                caseSensitive: true
            );
            included.Matches.Should().ContainSingle().Which.Line.Should().Be(1);
            var direct = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "A(b)",
                "Library/*.cs",
                true,
                literal: true
            );
            direct.Matches.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Oversized_matches_are_identified_instead_of_splitting_records()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "large.txt"),
                "MATCH" + new string('x', 90000)
            );
            var page = await WorkspaceGrepTools.SearchAsync(root, "", "MATCH", null, true);
            var match = page.Matches.Should().ContainSingle().Subject;
            match.Incomplete.Should().BeTrue();
            match.Path.Should().Be("large.txt");
            match.Line.Should().Be(1);
            page.Content.Length.Should().BeLessThan(65536);
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
