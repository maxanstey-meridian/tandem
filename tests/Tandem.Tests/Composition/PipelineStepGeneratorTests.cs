using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tandem.Generators;

namespace Tandem.Tests.Composition;

public sealed class PipelineStepGeneratorTests
{
    [Fact]
    public void Generator_ReadsAuthoredUnionWithoutDunetGeneratedOutput()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Tandem
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class PipelineStageAttribute(string id) : Attribute;
                public sealed record ResultCase<TState, TResult, TCase>;
                public interface IGeneratedPipelineStep<TState, TResult>;
                public readonly struct GeneratedStepCompletion;
                public static class PipelineResultPayload;
            }

            namespace Tandem.Domain
            {
                public sealed record PipelineMessage<TState>(TState State);
            }

            namespace Dunet
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class UnionAttribute : Attribute;
            }

            namespace Consumer
            {
                using Dunet;
                using Tandem;
                using Tandem.Domain;

                public sealed record State(int Count);

                [PipelineStage("verification")]
                public sealed partial class VerificationStage
                {
                    [Union]
                    public partial record VerificationResult
                    {
                        public partial record Passed(State State);
                        public partial record Failed(State State);
                    }

                    public ValueTask<VerificationResult> ExecuteAsync(
                        State state,
                        CancellationToken cancellationToken
                    ) => throw new NotImplementedException();
                }
            }
            """;
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "GeneratorProof",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new PipelineStepGenerator().AsSourceGenerator()
        );

        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();

        result.Diagnostics.Should().BeEmpty();
        var generated = result.Results.Single().GeneratedSources.Single().SourceText.ToString();
        generated.Should().Contain("public string Id => \"verification\"");
        generated.Should().Contain("Passed =>");
        generated.Should().Contain("Failed =>");
        generated.Should().Contain("State = value.State");
        generated.Should().Contain("GeneratedCustomStepDescriptor");
        generated.Should().NotContain("IGeneratedPipelineNode");
        generated.Should().NotContain("Microsoft.Agents");
    }

    [Theory]
    [InlineData("public sealed class VerificationStage", "TANDEM001")]
    [InlineData("public sealed partial class VerificationStage", "TANDEM002")]
    public void Generator_ReportsStableDiagnosticsForInvalidAuthoredContracts(
        string declaration,
        string diagnosticId
    )
    {
        var cases =
            diagnosticId == "TANDEM002"
                ? "public partial record Passed(State Value);"
                : "public partial record Passed(State State);";
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            namespace Tandem
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class PipelineStageAttribute(string id) : Attribute;
            }
            namespace Tandem.Domain { public sealed record PipelineMessage<TState>(TState State); }
            namespace Dunet
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class UnionAttribute : Attribute;
            }
            namespace Consumer
            {
                using Dunet;
                using Tandem;
                using Tandem.Domain;
                public sealed record State(int Count);
                [PipelineStage("verification")]
                {{declaration}}
                {
                    [Union]
                    public partial record VerificationResult { {{cases}} }
                    public ValueTask<VerificationResult> ExecuteAsync(
                        State state,
                        CancellationToken cancellationToken
                    ) => throw new NotImplementedException();
                }
            }
            """;

        var result = RunGenerator(source);

        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Id == diagnosticId);
        result.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Theory]
    [InlineData(
        "State",
        "Consumer.CancellationToken",
        "System.Threading.Tasks.ValueTask<VerificationResult>"
    )]
    [InlineData(
        "State",
        "System.Threading.CancellationToken",
        "Consumer.ValueTask<VerificationResult>"
    )]
    public void Generator_RejectsLookalikeFrameworkTypes(
        string messageType,
        string cancellationType,
        string returnType
    )
    {
        var source = $$"""
            using System;
            namespace Tandem
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class PipelineStageAttribute(string id) : Attribute;
            }
            namespace Tandem.Domain { public sealed record PipelineMessage<TState>(TState State); }
            namespace Consumer
            {
                public sealed record State;
                public sealed record PipelineMessage<T>(T State);
                public sealed record CancellationToken;
                public sealed record ValueTask<T>;
                [Tandem.PipelineStage("verification")]
                public sealed partial class VerificationStage
                {
                    public partial record VerificationResult
                    {
                        public partial record Passed(State State);
                    }
                    public {{returnType}} ExecuteAsync(
                        {{messageType}} pipeline,
                        {{cancellationType}} cancellationToken
                    ) => throw new NotImplementedException();
                }
            }
            """;

        var result = RunGenerator(source);

        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Id == "TANDEM001");
        result.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Generator_PreservesExecutionEnvelopeIndependentlyOfResultShape()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            namespace Tandem
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class PipelineStageAttribute(string id) : Attribute;
            }
            namespace Tandem.Domain { public sealed record PipelineMessage<TState>(TState State); }
            namespace Dunet
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class UnionAttribute : Attribute;
            }
            namespace Consumer
            {
                using Dunet;
                public sealed record State;
                public sealed record Outcome;
                [Tandem.PipelineStage("mixed")]
                public sealed partial class MixedStage
                {
                    [Union]
                    public partial record MixedResult
                    {
                        public partial record WithOutcome(State State, Outcome Outcome);
                        public partial record WithoutOutcome(State State);
                    }
                    public ValueTask<MixedResult> ExecuteAsync(
                        State state,
                        CancellationToken cancellationToken
                    ) => throw new NotImplementedException();
                }
            }
            """;

        var generated = RunGenerator(source)
            .Results.Single()
            .GeneratedSources.Single()
            .SourceText.ToString();

        generated.Should().NotContain("LatestOutcome = value.Outcome");
        generated.Should().NotContain("LatestOutcome = null");
        generated.Should().NotContain("Runtime = value.Runtime");
    }

    [Theory]
    [InlineData("ValueTask", "GeneratedPassThroughStepDescriptor", false)]
    [InlineData("ValueTask<State>", "GeneratedStateStepDescriptor", false)]
    [InlineData("ValueTask<Outcome<State>>", "GeneratedOutcomeStepDescriptor", true)]
    public void Generator_InfersStandardStepModes(
        string returnType,
        string descriptor,
        bool hasResultSelectors
    )
    {
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            namespace Tandem
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class PipelineStageAttribute(string id) : Attribute;
            }
            namespace Tandem.Domain
            {
                public abstract record Outcome<T>;
            }
            namespace Consumer
            {
                using Tandem;
                using Tandem.Domain;
                public sealed record State;
                [PipelineStage("stage")]
                public sealed partial class Stage
                {
                    public {{returnType}} ExecuteAsync(State state, CancellationToken cancellationToken) =>
                        throw new NotImplementedException();
                }
            }
            """;

        var result = RunGenerator(source);

        result.Diagnostics.Should().BeEmpty();
        var generated = result.Results.Single().GeneratedSources.Single().SourceText.ToString();
        generated.Should().Contain(descriptor);
        generated.Contains("public ResultRoutes Result").Should().Be(hasResultSelectors);
        if (hasResultSelectors)
        {
            generated.Should().Contain("Success =>");
            generated.Should().Contain("Failed =>");
        }
    }

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "GeneratorDiagnosticProof",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new PipelineStepGenerator().AsSourceGenerator()
        );
        return driver.RunGenerators(compilation).GetRunResult();
    }
}
