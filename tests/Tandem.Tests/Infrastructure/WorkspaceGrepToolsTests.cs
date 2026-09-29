using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

#pragma warning disable MAAI001

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

            var page = await WorkspaceGrepTools.SearchAsync(root, "", "match", null, true, 0, 500);

            page.Content.Should()
                .Be("a/one.txt:1:match one\na/one.txt:2:MATCH again\nz/two.txt:2:MATCH two\n");
            page.NextOffset.Should().BeNull();
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
                0,
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

    [Theory]
    [InlineData(
        "Rivet.Tool/**/*.cs",
        "Rivet.Tool/Nested/Child.cs:1:MATCH\nRivet.Tool/Program.cs:1:MATCH\n"
    )]
    [InlineData("rivet.tool/program.CS", "Rivet.Tool/Program.cs:1:MATCH\n")]
    [InlineData("Rivet.Tool\\Program.cs", "Rivet.Tool/Program.cs:1:MATCH\n")]
    public async Task SearchAsync_PathGlobsMatchOnlyTheirPrefix(string glob, string expected)
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
            var page = await WorkspaceGrepTools.SearchAsync(root, "", "MATCH", glob, true, 0, 500);
            page.Content.Should().Be(expected);
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
            var page = await WorkspaceGrepTools.SearchAsync(
                root,
                directory,
                "MATCH",
                glob,
                recursive,
                0,
                500
            );
            page.Content.Should().BeEmpty();
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
                0,
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

            var first = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                limit: 2
            );
            first.Matches.Select(m => m.Text).Should().Equal("match😀", "match2");
            first.NextOffset.Should().Be(2);
            var second = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                offset: 2,
                limit: 2
            );
            second.Matches.Select(m => m.Path).Should().Equal("b.txt", "b.txt");
            second.NextOffset.Should().Be(4);
            var third = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                offset: 4,
                limit: 2
            );
            third.NextOffset.Should().BeNull();
            first
                .Matches.Concat(second.Matches)
                .Concat(third.Matches)
                .Select(m => (m.Path, m.Line))
                .Should()
                .OnlyHaveUniqueItems()
                .And.HaveCount(6);
            // Continuation is stateless: after an edit the same offset honestly
            // reflects the repository instead of failing a version check.
            await File.AppendAllTextAsync(Path.Combine(root, "b.txt"), "match3\n");
            var afterEdit = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "match",
                null,
                true,
                offset: 2,
                limit: 500
            );
            afterEdit
                .Matches.Select(m => (m.Path, m.Line, m.Text))
                .Should()
                .Equal(
                    ("b.txt", 1, "match😀"),
                    ("b.txt", 2, "match2"),
                    ("b.txt", 3, "match3"),
                    ("c.txt", 1, "match😀"),
                    ("c.txt", 2, "match2")
                );
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
            match.Text.Should().EndWith("…");
            page.SkippedCount.Should().Be(0);
            match.Path.Should().Be("large.txt");
            match.Line.Should().Be(1);
            page.Content.Length.Should().BeLessThan(65536);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Inside_git_ignored_files_are_skipped_and_tracked_build_directories_searched()
    {
        var root = CreateDirectory();
        try
        {
            await Git(root, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "generated/\n*.log\n");
            foreach (
                var path in new[]
                {
                    "bin/Tracked.cs",
                    "generated/Ignored.cs",
                    "node_modules/pkg/Untracked.js",
                    "src/Source.cs",
                    "trace.log",
                }
            )
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
                await File.WriteAllTextAsync(Path.Combine(root, path), "MATCH");
            }
            await Git(root, "add", "-f", "bin/Tracked.cs");

            var page = await WorkspaceGrepTools.SearchAsync(root, "", "MATCH", null, true);
            var named = await WorkspaceGrepTools.SearchAsync(
                root,
                "generated",
                "MATCH",
                null,
                true
            );
            var prefixed = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                "generated/*.cs",
                true
            );

            page.Matches.Select(m => m.Path)
                .Should()
                .Equal("bin/Tracked.cs", "node_modules/pkg/Untracked.js", "src/Source.cs");
            named.Matches.Select(m => m.Path).Should().Equal("generated/Ignored.cs");
            prefixed.Matches.Select(m => m.Path).Should().Equal("generated/Ignored.cs");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Outside_git_pruned_directories_are_not_traversed()
    {
        var root = CreateDirectory();
        var outside = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "node_modules", "pkg", "a.js"),
                "MATCH"
            );
            Directory.CreateSymbolicLink(
                Path.Combine(root, "node_modules", "pkg", "link"),
                outside
            );
            await File.WriteAllTextAsync(Path.Combine(root, "kept.txt"), "MATCH");

            var pruned = await WorkspaceGrepTools.SearchAsync(root, "", "MATCH", null, true);
            var included = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                null,
                true,
                includeExcluded: true
            );

            pruned.Matches.Select(m => m.Path).Should().Equal("kept.txt");
            pruned.SkippedCount.Should().Be(0, "the pruned directory's link is never visited");
            included
                .Matches.Select(m => m.Path)
                .Should()
                .Equal("kept.txt", "node_modules/pkg/a.js");
            included.Skipped.Should().Equal("node_modules/pkg/link: symbolic link");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_full_page_stops_before_reading_later_files(bool insideGit)
    {
        var root = CreateDirectory();
        try
        {
            if (insideGit)
            {
                await Git(root, "init", "-q");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "MATCH\nMATCH\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "z.txt"), [0xff, 0xfe, 0xfd]);

            var first = await WorkspaceGrepTools.SearchAsync(
                root,
                "",
                "MATCH",
                null,
                true,
                limit: 1
            );
            var complete = await WorkspaceGrepTools.SearchAsync(root, "", "MATCH", null, true);

            first.NextOffset.Should().Be(1);
            first.SkippedCount.Should().Be(0, "z.txt is never opened once the page is full");
            complete.Skipped.Should().ContainSingle().Which.Should().StartWith("z.txt:");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task Git(string root, params string[] arguments) =>
        (await LocalProcess.RunAsync(new LocalProcessRequest("git", arguments, root)))
            .ExitCode.Should()
            .Be(0);

    [Fact]
    public void Grep_page_payload_carries_no_derivable_fields_or_instructions()
    {
        var final = JsonSerializer.SerializeToElement(
            new WorkspaceGrepTools.GrepPage([], 0, [], null)
        );
        final
            .EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo("matches", "skippedCount", "skipped");
        var continued = JsonSerializer.SerializeToElement(
            new WorkspaceGrepTools.GrepPage([], 0, [], 2)
        );
        continued.GetProperty("nextOffset").GetInt32().Should().Be(2);
        foreach (var json in new[] { final, continued })
        {
            json.TryGetProperty("hasMore", out _).Should().BeFalse();
            json.TryGetProperty("returnedCount", out _).Should().BeFalse();
            json.TryGetProperty("exclusionsApplied", out _).Should().BeFalse();
            json.TryGetProperty("nextCursor", out _).Should().BeFalse();
            json.TryGetProperty("pagination", out _).Should().BeFalse();
        }
    }

    [Fact]
    public void Grep_schema_describes_skips_in_prose_without_directory_enumeration()
    {
        var tool = WorkspaceGrepTools.Create(Path.GetTempPath());
        var schema = tool.JsonSchema.GetRawText();
        foreach (var entry in WorkspaceSearchPolicy.ExcludedDirectories)
        {
            // Word-boundary matching avoids false positives such as "Output"
            // containing the directory "out"; dotted names are distinctive enough
            // for containment matching.
            var pattern =
                char.IsLetterOrDigit(entry[0]) || entry[0] == '_'
                    ? $@"\b{Regex.Escape(entry)}\b"
                    : Regex.Escape(entry);
            Regex
                .IsMatch(schema, pattern, RegexOptions.IgnoreCase)
                .Should()
                .BeFalse($"the schema must not enumerate excluded directory '{entry}'");
        }

        // Skip behavior is documented once per request in the tool description,
        // not as an inlined directory enumeration in the schema.
        tool.Description.Should().Contain("reported in the result");
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tandem-grep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
