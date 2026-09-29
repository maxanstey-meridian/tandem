using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class WorkspaceGrepToolsTests
{
    [Fact]
    public async Task SearchAsync_ReturnsDeterministicNormalizedRecursiveResults()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
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
        using var temp = new TempDirectory();
        var root = temp.Path;
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

    [Fact]
    public async Task SearchAsync_RegexExecutionIsBounded()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        await File.WriteAllTextAsync(
            Path.Combine(root, "catastrophic.txt"),
            new string('a', 100_000) + "!"
        );

        var search = () => WorkspaceGrepTools.SearchAsync(root, "", "^(a+)+$", null, true, 0, 10);

        await search
            .Should()
            .ThrowAsync<System.Text.RegularExpressions.RegexMatchTimeoutException>();
    }

    [Fact]
    public async Task SearchAsync_RejectsEscapesMissingDirectoriesAndReparsePoints()
    {
        using var temp = new TempDirectory();
        var parent = temp.Path;
        var root = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(root);
        var escape = () => WorkspaceGrepTools.SearchAsync(root, "..", "x", null, true, 0, 10);
        await escape.Should().ThrowAsync<UnauthorizedAccessException>();
        var missing = () => WorkspaceGrepTools.SearchAsync(root, "missing", "x", null, true, 0, 10);
        await missing.Should().ThrowAsync<DirectoryNotFoundException>();

        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, outside);
        var linked = () => WorkspaceGrepTools.SearchAsync(root, "link", "x", null, true, 0, 10);
        await linked.Should().ThrowAsync<UnauthorizedAccessException>();
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
        using var temp = new TempDirectory();
        var root = temp.Path;
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
        using var temp = new TempDirectory();
        var root = temp.Path;
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

    [Fact]
    public async Task SearchAsync_SearchesSourceInsidePackages()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        Directory.CreateDirectory(Path.Combine(root, "packages", "sdk"));
        await File.WriteAllTextAsync(Path.Combine(root, "packages", "sdk", "Source.cs"), "MATCH");
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

    [Fact]
    public async Task Pages_preserve_records_and_skip_completed_files()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(root, name), "match😀\nmatch2\n");
        }

        var first = await WorkspaceGrepTools.SearchAsync(root, "", "match", null, true, limit: 2);
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

    [Fact]
    public async Task Exclusions_literal_case_and_skips_are_explicit()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
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

    [Fact]
    public async Task Oversized_matches_are_identified_instead_of_splitting_records()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
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
        var options = new ChatOptions();
        WorkspaceGrepTools.Add(options, Path.GetTempPath());
        var tool = (AIFunction)options.Tools!.Single();
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
}
