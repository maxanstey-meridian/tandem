using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Tandem.Tests.Infrastructure;

public sealed class LinePageReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continuation_reconstructs_long_lines_and_unicode_without_loss(bool utf16)
    {
        var path = Path.GetTempFileName();
        try
        {
            var first = new string('x', 65500) + "\uD83D\uDE00" + new string('y', 90000);
            await File.WriteAllTextAsync(
                path,
                first + "\r\nlast",
                utf16 ? Encoding.Unicode : new UTF8Encoding(false)
            );
            var builders = new Dictionary<int, StringBuilder>();
            var startLine = 1;
            var characterOffset = 0;
            var midLineResumes = 0;
            while (true)
            {
                var page = await BoundedLinePageReader.ReadAsync(
                    path,
                    startLine: startLine,
                    lineCount: 2000,
                    characterOffset: characterOffset
                );
                page.StartLine.Should().Be(startLine);
                for (var i = 0; i < page.Lines.Count; i++)
                {
                    var line = page.StartLine + i;
                    if (!builders.TryGetValue(line, out var builder))
                    {
                        builder = new StringBuilder();
                        builders[line] = builder;
                    }

                    builder.Append(page.Lines[i]);
                }

                if (page.NextStartLine is null)
                {
                    page.NextCharacterOffset.Should().BeNull();
                    break;
                }

                if (page.NextCharacterOffset is not null)
                {
                    midLineResumes++;
                    page.Lines.Should().NotBeEmpty();
                }

                startLine = page.NextStartLine.Value;
                characterOffset = page.NextCharacterOffset ?? 0;
            }

            // The first line exceeds 155,000 characters, so mid-line resume positions
            // must be followed more than once across pages.
            midLineResumes.Should().BeGreaterThan(1);
            builders.Should().HaveCount(2);
            builders[1].ToString().Should().Be(first);
            builders[2].ToString().Should().Be("last");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Continuation_supports_changing_page_sizes_and_is_independent_per_file()
    {
        var path = Path.GetTempFileName();
        var otherPath = Path.GetTempFileName();
        try
        {
            var expected = Enumerable.Range(1, 450).Select(i => $"line {i}").ToArray();
            await File.WriteAllLinesAsync(path, expected);
            await File.WriteAllLinesAsync(otherPath, expected);
            var actual = new List<string>();
            var startLine = 1;
            int? nextStartLine = null;
            foreach (var count in new[] { 130, 120, 200 })
            {
                var page = await BoundedLinePageReader.ReadAsync(
                    path,
                    startLine: startLine,
                    lineCount: count
                );
                actual.AddRange(page.Lines);
                nextStartLine = page.NextStartLine;
                startLine = page.NextStartLine ?? startLine;
            }

            nextStartLine.Should().BeNull();
            actual.Should().Equal(expected);
            // Continuation is stateless: an identical ordinal on another file is an
            // independent read, not a continuation rejection.
            var other = await BoundedLinePageReader.ReadAsync(
                otherPath,
                startLine: 131,
                lineCount: 120
            );
            other.Lines.First().Should().Be("line 131");
        }
        finally
        {
            File.Delete(path);
            File.Delete(otherPath);
        }
    }

    [Fact]
    public async Task Short_page_stops_without_decoding_the_entire_suffix_and_continuation_is_ordinary_validation()
    {
        var path = Path.GetTempFileName();
        try
        {
            // If the reader scans the complete file, the late NUL fails this call.
            await File.WriteAllTextAsync(path, "one\ntwo\n" + new string('x', 100000) + "\0");
            var page = JsonSerializer.SerializeToElement(
                await BoundedLinePageReader.ReadAsync(path, lineCount: 1)
            );
            page.GetProperty("lines")[0].GetString().Should().Be("one");
            page.GetProperty("startLine").GetInt32().Should().Be(1);
            page.GetProperty("nextStartLine").GetInt32().Should().Be(2);
            page.TryGetProperty("totalLines", out _).Should().BeFalse();
            page.TryGetProperty("hasMore", out _).Should().BeFalse();
            page.TryGetProperty("pagination", out _).Should().BeFalse();
            page.TryGetProperty("nextCursor", out _).Should().BeFalse();
            // After an edit there is no version state to invalidate: the stale line
            // ordinal honestly reflects the new repository as an empty final page.
            await File.WriteAllTextAsync(path, "changed");
            var after = await BoundedLinePageReader.ReadAsync(path, startLine: 2);
            after.StartLine.Should().Be(2);
            after.Lines.Should().BeEmpty();
            after.NextStartLine.Should().BeNull();
            // A request beyond the file is ordinary argument validation.
            await FluentActions
                .Awaiting(() => BoundedLinePageReader.ReadAsync(path, startLine: 5))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Empty_file_and_out_of_range_lines_are_explicit()
    {
        var path = Path.GetTempFileName();
        try
        {
            var page = JsonSerializer.SerializeToElement(
                await BoundedLinePageReader.ReadAsync(path)
            );
            page.GetProperty("startLine").GetInt32().Should().Be(1);
            page.GetProperty("lines").GetArrayLength().Should().Be(0);
            page.TryGetProperty("nextStartLine", out _).Should().BeFalse();
            await File.WriteAllTextAsync(path, "one\ntwo");
            await FluentActions
                .Awaiting(() => BoundedLinePageReader.ReadAsync(path, 4))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
