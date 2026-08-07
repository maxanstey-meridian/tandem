using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Tandem.Generators;

[Generator]
public sealed class PipelineStepGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "TANDEM001",
        "Invalid pipeline stage declaration",
        "Pipeline stage '{0}' must be a partial class with one ExecuteAsync method accepting PipelineMessage<TState> and CancellationToken and returning ValueTask<TResult>",
        "Tandem.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    private static readonly DiagnosticDescriptor InvalidResult = new(
        "TANDEM002",
        "Invalid pipeline stage result",
        "Result union '{0}' must declare at least one nested positional record case whose first parameter is named State",
        "Tandem.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var steps = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Tandem.PipelineStageAttribute",
            static (node, _) => node is ClassDeclarationSyntax,
            static (attributeContext, _) => CreateModel(attributeContext)
        );

        context.RegisterSourceOutput(
            steps,
            static (productionContext, result) =>
            {
                if (result.Diagnostic is { } diagnostic)
                {
                    productionContext.ReportDiagnostic(diagnostic);
                    return;
                }

                var model = result.Model!;
                productionContext.AddSource(
                    $"{model.Namespace}.{model.Name}.PipelineStep.g.cs",
                    SourceText.From(Render(model), Encoding.UTF8)
                );
            }
        );
    }

    private static StepGenerationResult CreateModel(GeneratorAttributeSyntaxContext context)
    {
        var step = (INamedTypeSymbol)context.TargetSymbol;
        var syntax = (ClassDeclarationSyntax)context.TargetNode;
        var id = (string)context.Attributes[0].ConstructorArguments[0].Value!;
        var compilation = context.SemanticModel.Compilation;
        var pipelineMessageType = compilation.GetTypeByMetadataName(
            "Tandem.Domain.PipelineMessage`1"
        );
        var cancellationTokenType = compilation.GetTypeByMetadataName(
            "System.Threading.CancellationToken"
        );
        var valueTaskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        var executeMethods = step.GetMembers("ExecuteAsync")
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == 2)
            .ToArray();
        if (
            !syntax.Modifiers.Any(modifier => modifier.ValueText == "partial")
            || string.IsNullOrWhiteSpace(id)
            || executeMethods.Length != 1
            || executeMethods[0].Parameters[0].Type is not INamedTypeSymbol input
            || !SymbolEqualityComparer.Default.Equals(input.OriginalDefinition, pipelineMessageType)
            || !SymbolEqualityComparer.Default.Equals(
                executeMethods[0].Parameters[1].Type,
                cancellationTokenType
            )
            || executeMethods[0].ReturnType is not INamedTypeSymbol valueTask
            || !SymbolEqualityComparer.Default.Equals(valueTask.OriginalDefinition, valueTaskType)
            || valueTask.TypeArguments[0] is not INamedTypeSymbol result
        )
        {
            return StepGenerationResult.Failure(
                Diagnostic.Create(InvalidDeclaration, syntax.Identifier.GetLocation(), step.Name)
            );
        }

        var stateType = input
            .TypeArguments[0]
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var resultSyntax = syntax
            .Members.OfType<RecordDeclarationSyntax>()
            .SingleOrDefault(record => record.Identifier.ValueText == result.Name);
        if (resultSyntax is null)
        {
            return StepGenerationResult.Failure(
                Diagnostic.Create(InvalidResult, syntax.Identifier.GetLocation(), result.Name)
            );
        }

        var caseSyntax = resultSyntax.Members.OfType<RecordDeclarationSyntax>().ToArray();
        if (
            caseSyntax.Length == 0
            || caseSyntax.Any(record =>
                record.ParameterList?.Parameters.FirstOrDefault()?.Identifier.ValueText != "State"
            )
        )
        {
            return StepGenerationResult.Failure(
                Diagnostic.Create(InvalidResult, resultSyntax.Identifier.GetLocation(), result.Name)
            );
        }

        var cases = resultSyntax
            .Members.OfType<RecordDeclarationSyntax>()
            .Select(record => new CaseModel(
                record.Identifier.ValueText,
                record.ParameterList?.Parameters.Any(parameter =>
                    parameter.Identifier.ValueText == "Runtime"
                ) == true,
                record.ParameterList?.Parameters.Any(parameter =>
                    parameter.Identifier.ValueText == "Outcome"
                ) == true
            ))
            .ToImmutableArray();

        return StepGenerationResult.Success(
            new StepModel(
                step.ContainingNamespace.ToDisplayString(),
                step.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
                step.Name,
                id,
                stateType,
                result.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                cases
            )
        );
    }

    private static string Render(StepModel model)
    {
        var cases = string.Join(
            "\n",
            model.Cases.Select(resultCase =>
                $$"""
                            public global::Tandem.ResultCase<{{model.StateType}}, {{model.ResultType}}, {{model.ResultType}}.{{resultCase.Name}}> {{resultCase.Name}} =>
                                new(step, "{{resultCase.Name}}");
                    """
            )
        );
        var adaptations = string.Join(
            "\n",
            model.Cases.Select(resultCase =>
                $$"""
                            {{model.ResultType}}.{{resultCase.Name}} value => pipeline with
                            {
                                {{(resultCase.HasRuntime ? "Runtime = value.Runtime," : "")}}
                                State = value.State,
                                LatestOutcome = {{(
                        resultCase.HasOutcome ? "value.Outcome" : "null"
                    )}},
                                LatestResult = global::Tandem.PipelineResultPayload.Create(Id, "{{resultCase.Name}}", value),
                            },
                    """
            )
        );

        return $$"""
            // <auto-generated />
            #nullable enable

            namespace {{model.Namespace}};

            {{model.Accessibility}} sealed partial class {{model.Name}}
                : global::Tandem.IGeneratedPipelineStep<{{model.StateType}}, {{model.ResultType}}>
            {
                public string Id => "{{model.Id}}";

                [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
                public global::Tandem.PipelineNodeDescriptor Descriptor =>
                    new global::Tandem.GeneratedPipelineStepDescriptor<{{model.StateType}}, {{model.ResultType}}>(
                        Id,
                        ExecuteAsync,
                        AdaptResult
                    );

                public ResultRoutes Result => new(this);

                public readonly struct ResultRoutes({{model.Name}} step)
                {
            {{cases}}
                }

                public global::Tandem.Domain.PipelineMessage<{{model.StateType}}> AdaptResult(
                    global::Tandem.Domain.PipelineMessage<{{model.StateType}}> pipeline,
                    {{model.ResultType}} result
                ) => result switch
                {
            {{adaptations}}
                    _ => throw new global::System.InvalidOperationException("Unknown result case."),
                };
            }
            """;
    }

    private sealed class StepModel
    {
        public StepModel(
            string @namespace,
            string accessibility,
            string name,
            string id,
            string stateType,
            string resultType,
            ImmutableArray<CaseModel> cases
        )
        {
            Namespace = @namespace;
            Accessibility = accessibility;
            Name = name;
            Id = id;
            StateType = stateType;
            ResultType = resultType;
            Cases = cases;
        }

        public string Namespace { get; }
        public string Accessibility { get; }
        public string Name { get; }
        public string Id { get; }
        public string StateType { get; }
        public string ResultType { get; }
        public ImmutableArray<CaseModel> Cases { get; }
    }

    private sealed class StepGenerationResult
    {
        private StepGenerationResult(StepModel? model, Diagnostic? diagnostic)
        {
            Model = model;
            Diagnostic = diagnostic;
        }

        public StepModel? Model { get; }
        public Diagnostic? Diagnostic { get; }

        public static StepGenerationResult Success(StepModel model) => new(model, null);

        public static StepGenerationResult Failure(Diagnostic diagnostic) => new(null, diagnostic);
    }

    private sealed class CaseModel
    {
        public CaseModel(string name, bool hasRuntime, bool hasOutcome)
        {
            Name = name;
            HasRuntime = hasRuntime;
            HasOutcome = hasOutcome;
        }

        public string Name { get; }
        public bool HasRuntime { get; }
        public bool HasOutcome { get; }
    }
}
