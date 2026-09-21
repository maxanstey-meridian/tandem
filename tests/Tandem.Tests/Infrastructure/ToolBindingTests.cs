using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class ToolBindingTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"count\":{}}")]
    [InlineData("{\"count\":\"not a number\"}")]
    public async Task Framework_rejects_missing_or_invalid_typed_arguments_before_execution(
        string json
    )
    {
        var executed = false;
        var function = AIFunctionFactory.Create(
            (int count) =>
            {
                executed = true;
                return count;
            }
        );
        var arguments = new AIFunctionArguments(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!
        );
        var invoke = async () => await function.InvokeAsync(arguments);
        await invoke.Should().ThrowAsync<Exception>();
        executed.Should().BeFalse();
    }

    [Fact]
    public void Unknown_arguments_are_rejected_at_the_tool_boundary()
    {
        var function = AIFunctionFactory.Create((int count) => count);
        var arguments = new AIFunctionArguments { ["count"] = 1, ["typo"] = 2 };
        var validate = () => ToolInputValidation.ValidateArguments(function, arguments);
        validate.Should().Throw<ToolInputException>().WithMessage("*typo*");
    }

    [Fact]
    public void Expected_workspace_errors_follow_registered_semantics()
    {
        var read = new ToolSemantics(Tandem.Infrastructure.ToolEffect.Read);
        ToolInputValidation.IsExpected(new FileNotFoundException(), read).Should().BeTrue();
        ToolInputValidation
            .IsExpected(new ArgumentException("invalid offset"), read)
            .Should()
            .BeTrue();
        ToolInputValidation.IsExpected(new IOException("disk failure"), read).Should().BeFalse();
        ToolInputValidation.IsExpected(new OperationCanceledException(), read).Should().BeFalse();
    }

    [Fact]
    public void Only_declared_input_errors_are_classified_as_expected()
    {
        ToolInputValidation.IsExpected(new ToolInputException("bad input")).Should().BeTrue();
        ToolInputValidation
            .IsExpected(new WorkspacePathException("outside workspace"))
            .Should()
            .BeTrue();
        ToolInputValidation.IsExpected(new IOException("disk failure")).Should().BeFalse();
        ToolInputValidation.IsExpected(new InvalidOperationException("bug")).Should().BeFalse();
        ToolInputValidation.IsExpected(new OperationCanceledException()).Should().BeFalse();
        ToolInputValidation
            .IsExpected(new ArgumentException("bug in a handler"))
            .Should()
            .BeFalse();
    }
}
