using System.Text;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class BoundedTextPageReaderTests
{
    [Fact]
    public async Task Pages_reconstruct_exact_text_without_splitting_surrogates()
    {
        var path = Path.GetTempFileName();
        var text = "BMP Ω\r\nline\n😀tail" + new string('x', 70_000);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        try
        {
            var reconstructed = new StringBuilder();
            var offset = 0;
            do
            {
                var page = await BoundedTextPageReader.ReadAsync(path, offset, 9);
                page.Content.Should().NotBeEmpty();
                char.IsLowSurrogate(page.Content[0]).Should().BeFalse();
                char.IsHighSurrogate(page.Content[^1]).Should().BeFalse();
                reconstructed.Append(page.Content);
                if (!page.HasMore)
                {
                    page.NextOffset.Should().BeNull();
                    break;
                }
                page.NextOffset.Should().Be(offset + page.Length);
                offset = page.NextOffset!.Value;
            } while (true);

            reconstructed.ToString().Should().Be(text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Range_validation_and_end_page_are_explicit()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "abc😀");
        try
        {
            (await BoundedTextPageReader.ReadAsync(path, 5, 10))
                .Should()
                .BeEquivalentTo(new TextPage("", 5, 0, 5, false, null));
            await FluentActions
                .Awaiting(() => BoundedTextPageReader.ReadAsync(path, -1, 1))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
            await FluentActions
                .Awaiting(() => BoundedTextPageReader.ReadAsync(path, 0, 0))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
            await FluentActions
                .Awaiting(() => BoundedTextPageReader.ReadAsync(path, 0, 65_537))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
            await FluentActions
                .Awaiting(() => BoundedTextPageReader.ReadAsync(path, 6, 1))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
            await FluentActions
                .Awaiting(() => BoundedTextPageReader.ReadAsync(path, 4, 1))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
            await FluentActions
                .Awaiting(() => BoundedTextPageReader.ReadAsync(path, 3, 1))
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Actual_workspace_read_tool_has_range_schema_and_structured_result()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tandem-read-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "small.txt"), "small");
        try
        {
            var options = new ChatOptions();
            WorkspaceFileReadTools.Add(options, directory);
            var tool = (AIFunction)options.Tools!.Single();

            tool.Name.Should().Be(FileAccessProvider.ReadFileToolName);
            tool.JsonSchema.GetProperty("properties")
                .EnumerateObject()
                .Select(p => p.Name)
                .Should()
                .BeEquivalentTo("path", "offset", "limit");
            var properties = tool.JsonSchema.GetProperty("properties");
            properties.GetProperty("offset").GetProperty("default").GetInt32().Should().Be(0);
            properties.GetProperty("limit").GetProperty("default").GetInt32().Should().Be(65_536);
            properties
                .GetProperty("offset")
                .GetProperty("description")
                .GetString()
                .Should()
                .Contain("UTF-16");
            properties
                .GetProperty("limit")
                .GetProperty("description")
                .GetString()
                .Should()
                .Contain("65536");
            var result = await tool.InvokeAsync(new AIFunctionArguments { ["path"] = "small.txt" });
            var json = result.Should().BeOfType<System.Text.Json.JsonElement>().Subject;
            json.GetProperty("content").GetString().Should().Be("small");
            json.GetProperty("offset").GetInt32().Should().Be(0);
            json.GetProperty("length").GetInt32().Should().Be(5);
            json.GetProperty("totalLength").GetInt32().Should().Be(5);
            json.GetProperty("hasMore").GetBoolean().Should().BeFalse();
            json.TryGetProperty("nextOffset", out _).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("/absolute.txt")]
    [InlineData("../outside.txt")]
    [InlineData(".GiT/config")]
    public async Task Actual_workspace_read_tool_rejects_unauthorized_paths(string path)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tandem-read-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ChatOptions();
            WorkspaceFileReadTools.Add(options, directory);
            var tool = (AIFunction)options.Tools!.Single();
            await FluentActions
                .Awaiting(() =>
                    tool.InvokeAsync(new AIFunctionArguments { ["path"] = path }).AsTask()
                )
                .Should()
                .ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Actual_workspace_read_tool_rejects_symbolic_link_escape()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            "tandem-read-link-" + Guid.NewGuid().ToString("N")
        );
        var workspace = Path.Combine(parent, "workspace");
        var outside = Path.Combine(parent, "outside.txt");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), outside);
        try
        {
            var options = new ChatOptions();
            WorkspaceFileReadTools.Add(options, workspace);
            var tool = (AIFunction)options.Tools!.Single();
            await FluentActions
                .Awaiting(() =>
                    tool.InvokeAsync(new AIFunctionArguments { ["path"] = "link.txt" }).AsTask()
                )
                .Should()
                .ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }
}
#pragma warning restore MAAI001
