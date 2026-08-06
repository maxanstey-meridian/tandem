using System.ClientModel.Primitives;
using System.Text;
using FluentAssertions;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

#pragma warning disable SCME0001

public sealed class OpenRouterReasoningChatClientTests
{
    [Fact]
    public void TryExtractReasoning_ReadsOpenRouterReasoningDelta()
    {
        var patch = new JsonPatch("{}"u8.ToArray());
        patch.Set("$.choices[0].delta.reasoning"u8, "Inspect the repository first.");

        var found = OpenRouterReasoningChatClient.TryExtractReasoning(patch, out var reasoning);

        found.Should().BeTrue();
        reasoning.Should().Be("Inspect the repository first.");
    }

    [Fact]
    public void TryExtractReasoning_IgnoresOrdinaryContent()
    {
        var patch = new JsonPatch(Encoding.UTF8.GetBytes("{}"));
        patch.Set("$.choices[0].delta.content"u8, "Done.");

        var found = OpenRouterReasoningChatClient.TryExtractReasoning(patch, out var reasoning);

        found.Should().BeFalse();
        reasoning.Should().BeEmpty();
    }
}

#pragma warning restore SCME0001
