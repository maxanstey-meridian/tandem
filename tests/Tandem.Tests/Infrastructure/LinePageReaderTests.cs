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
            var first = new string('x', 65500) + "😀" + new string('y', 90000);
            await File.WriteAllTextAsync(
                path,
                first + "\r\nlast",
                utf16 ? Encoding.Unicode : new UTF8Encoding(false)
            );
            var reconstructed = new StringBuilder();
            string? cursor = null;
            do
            {
                var page = JsonSerializer.SerializeToElement(
                    await BoundedLinePageReader.ReadAsync(path, cursor: cursor)
                );
                foreach (var line in page.GetProperty("lines").EnumerateArray())
                {
                    if (line.GetProperty("line").GetInt32() == 1)
                    {
                        reconstructed.Append(line.GetProperty("text").GetString());
                    }
                }

                cursor = page.GetProperty("nextCursor").GetString();
                if (cursor is null)
                {
                    page.GetProperty("totalLines").GetInt32().Should().Be(2);
                }
            } while (cursor is not null);
            reconstructed.ToString().Should().Be(first);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Continuation_allows_page_size_changes_but_rejects_another_file()
    {
        var path = Path.GetTempFileName();
        var otherPath = Path.GetTempFileName();
        try
        {
            var expected = Enumerable.Range(1, 450).Select(i => $"line {i}").ToArray();
            await File.WriteAllLinesAsync(path, expected);
            await File.WriteAllLinesAsync(otherPath, expected);
            var actual = new List<string>();
            string? cursor = null;
            foreach (var count in new[] { 130, 120, 200 })
            {
                var page = JsonSerializer.SerializeToElement(
                    await BoundedLinePageReader.ReadAsync(path, lineCount: count, cursor: cursor)
                );
                actual.AddRange(
                    page.GetProperty("lines")
                        .EnumerateArray()
                        .Select(line => line.GetProperty("text").GetString()!)
                );
                cursor = page.GetProperty("nextCursor").GetString();
                if (cursor is not null)
                {
                    await FluentActions
                        .Awaiting(() =>
                            BoundedLinePageReader.ReadAsync(
                                otherPath,
                                lineCount: count,
                                cursor: cursor
                            )
                        )
                        .Should()
                        .ThrowAsync<ArgumentException>();
                }
            }
            cursor.Should().BeNull();
            actual.Should().Equal(expected);
        }
        finally
        {
            File.Delete(path);
            File.Delete(otherPath);
        }
    }

    [Fact]
    public async Task Short_page_does_not_decode_the_entire_suffix_and_rejects_stale_continuation()
    {
        var path = Path.GetTempFileName();
        try
        {
            // If the reader scans the complete file, the late NUL fails this call.
            await File.WriteAllTextAsync(path, "one\ntwo\n" + new string('x', 100000) + "\0");
            var page = JsonSerializer.SerializeToElement(
                await BoundedLinePageReader.ReadAsync(path, lineCount: 1)
            );
            page.GetProperty("lines")[0].GetProperty("text").GetString().Should().Be("one");
            page.GetProperty("totalLines").ValueKind.Should().Be(JsonValueKind.Null);
            var cursor = page.GetProperty("nextCursor").GetString();
            await File.WriteAllTextAsync(path, "changed");
            await FluentActions
                .Awaiting(() => BoundedLinePageReader.ReadAsync(path, lineCount: 1, cursor: cursor))
                .Should()
                .ThrowAsync<ArgumentException>();
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
            page.GetProperty("totalLines").GetInt32().Should().Be(0);
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
