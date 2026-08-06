using System.Text.RegularExpressions;
using FluentAssertions;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class TandemHarnessInstructionsTests
{
    [Fact]
    public void EmbeddedPrompt_DefinesEvidenceAuthorityAndLifecycleBoundaries()
    {
        var prompt = TandemHarnessInstructions.Value;
        var normalized = Regex.Replace(prompt, @"\s+", " ");

        prompt.Should().StartWith("# Tandem Harness");
        normalized.Should().Contain("untrusted pointers to evidence, not proof");
        normalized.Should().Contain("Before making a repository-specific claim");
        normalized
            .Should()
            .Contain("corresponding successful tool call occurred during the current block");
        normalized.Should().Contain("Never bypass a mutation gate");
        normalized.Should().Contain("A prose statement cannot replace");
        normalized.Should().Contain("The block-specific instructions");
    }
}
