using FluentAssertions;
using Microsoft.Agents.AI;
#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class GitExcludedFileStoreTests
{
    [Fact]
    public async Task WriteAsync_StripsLeadingUnicodeBom()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tandem-file-store-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);

        try
        {
            var store = new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(directory));

            await store.WriteAsync(
                "service.ts",
                "\uFEFFimport type { Todo } from './types';\n",
                default
            );

            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "service.ts"));
            bytes.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
            (await File.ReadAllTextAsync(Path.Combine(directory, "service.ts")))
                .Should()
                .StartWith("import type");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_TruncatesLargeFilesBeforeReturningToolContent()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tandem-file-store-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "large.txt"),
                new string('x', 1_000_000)
            );
            var store = new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(directory));

            var content = await store.ReadAsync("large.txt", CancellationToken.None);

            content.Should().NotBeNull();
            content!.Length.Should().BeLessThan(70_000);
            content.Should().EndWith("[...truncated by Tandem...]");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_BoundsLargeResultSetsAndMatchingLines()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tandem-file-store-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            for (var file = 0; file < 20; file++)
            {
                var lines = Enumerable.Range(0, 20).Select(_ => $"MATCH {new string('x', 10_000)}");
                await File.WriteAllLinesAsync(Path.Combine(directory, $"large-{file}.txt"), lines);
            }
            var store = new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(directory));

            var results = await store.SearchAsync(
                "",
                "MATCH",
                "*.txt",
                recursive: true,
                CancellationToken.None
            );
            var characters = results.Sum(result =>
                result.FileName.Length
                + result.Snippet.Length
                + result.MatchingLines.Sum(match => match.Line.Length)
            );

            characters.Should().BeLessThan(100_000);
            results.Should().Contain(result => result.FileName == "[...truncated by Tandem...]");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("apps/api/obj/Debug/net10.0/SpeechScribe.Api.dll")]
    [InlineData("frontend/node_modules/package/index.js")]
    [InlineData("frontend/.next/server/chunks/app.js")]
    [InlineData("service/bin/Release/net10.0/service.dll")]
    [InlineData("python/.venv/lib/site-packages/module.py")]
    [InlineData("java/target/classes/App.class")]
    [InlineData("rust/target/debug/app")]
    [InlineData("ios/DerivedData/Build/Product")]
    [InlineData("android/.gradle/cache.bin")]
    [InlineData("terraform/.terraform/providers/plugin")]
    [InlineData("bazel-out/darwin-fastbuild/bin/app")]
    [InlineData("cmake-build-debug/CMakeFiles/app.dir/main.o")]
    [InlineData("windows\\TestResults\\run.trx")]
    public void SearchAsync_ExcludesStandardGeneratedAndDependencyDirectories(string path)
    {
        var result = SearchResult(path, "MATCH");

        GitExcludedFileStore.IsExcludedSearchResult(result).Should().BeTrue();
    }

    [Theory]
    [InlineData("src/native.so")]
    [InlineData("src/archive.nupkg")]
    [InlineData("src/image.png")]
    [InlineData("src/database.sqlite3")]
    [InlineData("src/font.woff2")]
    [InlineData("src/module.wasm")]
    public void SearchAsync_ExcludesBinaryFileExtensions(string path)
    {
        var result = SearchResult(path, "MATCH");

        GitExcludedFileStore.IsExcludedSearchResult(result).Should().BeTrue();
    }

    [Fact]
    public void SearchAsync_ExcludesBinaryLookingContentWithoutKnownExtension()
    {
        var result = SearchResult("src/generated.data", "MATCH\0\u0001\u0002\uFFFD");

        GitExcludedFileStore.IsExcludedSearchResult(result).Should().BeTrue();
    }

    [Theory]
    [InlineData("src/binocular/reader.cs")]
    [InlineData("src/object-model/record.cs")]
    [InlineData(".github/workflows/check.yml")]
    [InlineData(".vscode/settings.json")]
    [InlineData("src/package/catalog.ts")]
    public void SearchAsync_KeepsSourceAndUsefulConfigurationDirectories(string path)
    {
        var result = SearchResult(path, "MATCH");

        GitExcludedFileStore.IsExcludedSearchResult(result).Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_ReturnsSourceButNotGeneratedOrBinaryResults()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tandem-file-store-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "obj", "Debug"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "src", "keep.txt"), "MATCH");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "obj", "Debug", "skip.txt"),
                "MATCH"
            );
            await File.WriteAllTextAsync(Path.Combine(directory, "src", "skip.dll"), "MATCH");
            var store = new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(directory));

            var results = await store.SearchAsync(
                "",
                "MATCH",
                null,
                recursive: true,
                CancellationToken.None
            );

            results.Should().ContainSingle().Which.FileName.Should().Be("src/keep.txt");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FileSearchResult SearchResult(string path, string content) =>
        new()
        {
            FileName = path,
            Snippet = content,
            MatchingLines = [new FileSearchMatch { LineNumber = 1, Line = content }],
        };
}

#pragma warning restore MAAI001
