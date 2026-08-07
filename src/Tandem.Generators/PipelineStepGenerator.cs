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
        "Pipeline stage '{0}' must be a partial class with one ExecuteAsync method accepting TState and CancellationToken and returning ValueTask, ValueTask<TState>, ValueTask<Outcome<TState>>, or ValueTask<TNestedDunetUnion>",
        "Tandem.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    private static readonly DiagnosticDescriptor InvalidResult = new(
        "TANDEM002",
        "Invalid pipeline stage result",
        "Result union '{0}' must be a nested Dunet union with positional record cases whose first parameter is named State and has the pipeline state type",
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
        var id = (string?)context.Attributes[0].ConstructorArguments[0].Value;
        var compilation = context.SemanticModel.Compilation;
        var cancellationTokenType = compilation.GetTypeByMetadataName(
            "System.Threading.CancellationToken"
        );
        var valueTaskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        var genericValueTaskType = compilation.GetTypeByMetadataName(
            "System.Threading.Tasks.ValueTask`1"
        );
        var pipelineMessageType = compilation.GetTypeByMetadataName(
            "Tandem.Domain.PipelineMessage`1"
        );
        var outcomeType = compilation.GetTypeByMetadataName("Tandem.Domain.Outcome`1");
        var executeMethods = step.GetMembers("ExecuteAsync")
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == 2)
            .ToArray();

        if (
            !syntax.Modifiers.Any(modifier => modifier.ValueText == "partial")
            || string.IsNullOrWhiteSpace(id)
            || executeMethods.Length != 1
            || !SymbolEqualityComparer.Default.Equals(
                executeMethods[0].Parameters[1].Type,
                cancellationTokenType
            )
        )
        {
            return Invalid(syntax, step.Name);
        }

        var method = executeMethods[0];
        var inputType = method.Parameters[0].Type;
        var legacyEnvelopeInput =
            inputType is INamedTypeSymbol inputNamed
            && SymbolEqualityComparer.Default.Equals(
                inputNamed.OriginalDefinition,
                pipelineMessageType
            );
        var stateSymbol = legacyEnvelopeInput
            ? ((INamedTypeSymbol)inputType).TypeArguments[0]
            : inputType;
        var mode = StepMode.PassThrough;
        ITypeSymbol? resultSymbol = null;

        if (SymbolEqualityComparer.Default.Equals(method.ReturnType, valueTaskType))
        {
            if (legacyEnvelopeInput)
            {
                return Invalid(syntax, step.Name);
            }
        }
        else if (
            method.ReturnType is INamedTypeSymbol returnType
            && SymbolEqualityComparer.Default.Equals(
                returnType.OriginalDefinition,
                genericValueTaskType
            )
        )
        {
            resultSymbol = returnType.TypeArguments[0];
            if (
                !legacyEnvelopeInput
                && SymbolEqualityComparer.Default.Equals(resultSymbol, stateSymbol)
            )
            {
                mode = StepMode.State;
            }
            else if (
                !legacyEnvelopeInput
                && resultSymbol is INamedTypeSymbol outcome
                && SymbolEqualityComparer.Default.Equals(outcome.OriginalDefinition, outcomeType)
                && SymbolEqualityComparer.Default.Equals(outcome.TypeArguments[0], stateSymbol)
            )
            {
                mode = StepMode.Outcome;
            }
            else
            {
                mode = legacyEnvelopeInput ? StepMode.LegacyCustom : StepMode.Custom;
            }
        }
        else
        {
            return Invalid(syntax, step.Name);
        }

        var cases = ImmutableArray<CaseModel>.Empty;
        if (mode is StepMode.Custom or StepMode.LegacyCustom)
        {
            if (
                resultSymbol is not INamedTypeSymbol result
                || !SymbolEqualityComparer.Default.Equals(result.ContainingType, step)
                || !result
                    .GetAttributes()
                    .Any(attribute =>
                        attribute.AttributeClass?.ToDisplayString() == "Dunet.UnionAttribute"
                    )
            )
            {
                return InvalidResultAt(syntax, resultSymbol?.Name ?? "unknown");
            }

            var caseTypes = result
                .GetTypeMembers()
                .Where(type => !type.IsImplicitlyDeclared)
                .ToArray();
            if (
                caseTypes.Length == 0
                || caseTypes.Any(resultCase =>
                {
                    var constructor = resultCase.InstanceConstructors.FirstOrDefault(candidate =>
                        !candidate.IsImplicitlyDeclared || candidate.Parameters.Length > 0
                    );
                    return constructor is null
                        || constructor.Parameters.Length == 0
                        || constructor.Parameters[0].Name != "State"
                        || !SymbolEqualityComparer.Default.Equals(
                            constructor.Parameters[0].Type,
                            stateSymbol
                        );
                })
            )
            {
                return InvalidResultAt(syntax, result.Name);
            }

            cases = caseTypes.Select(type => new CaseModel(type.Name)).ToImmutableArray();
        }

        return StepGenerationResult.Success(
            new StepModel(
                step.ContainingNamespace.ToDisplayString(),
                step.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
                step.Name,
                id!,
                stateSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                resultSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                mode,
                cases
            )
        );
    }

    private static StepGenerationResult Invalid(ClassDeclarationSyntax syntax, string name) =>
        StepGenerationResult.Failure(
            Diagnostic.Create(InvalidDeclaration, syntax.Identifier.GetLocation(), name)
        );

    private static StepGenerationResult InvalidResultAt(
        ClassDeclarationSyntax syntax,
        string name
    ) =>
        StepGenerationResult.Failure(
            Diagnostic.Create(InvalidResult, syntax.Identifier.GetLocation(), name)
        );

    private static string Render(StepModel model)
    {
        var resultType = model.ResultType ?? "global::Tandem.GeneratedStepCompletion";
        var resultApi = model.Mode switch
        {
            StepMode.Outcome => $$"""

                    public ResultRoutes Result => new(this);

                    public readonly struct ResultRoutes({{model.Name}} step)
                    {
                        public global::Tandem.ResultCase<{{model.StateType}}, {{resultType}}, global::Tandem.Domain.Outcome<{{model.StateType}}>.Success> Success => new(step, "Success");
                        public global::Tandem.ResultCase<{{model.StateType}}, {{resultType}}, global::Tandem.Domain.Outcome<{{model.StateType}}>.Failed> Failed => new(step, "Failed");
                    }
                """,
            StepMode.Custom or StepMode.LegacyCustom => RenderCustomApi(model, resultType),
            _ => string.Empty,
        };
        var descriptor = model.Mode switch
        {
            StepMode.PassThrough =>
                $"new global::Tandem.GeneratedPassThroughStepDescriptor<{model.StateType}>(Id, ExecuteAsync)",
            StepMode.State =>
                $"new global::Tandem.GeneratedStateStepDescriptor<{model.StateType}>(Id, ExecuteAsync)",
            StepMode.Outcome =>
                $"new global::Tandem.GeneratedOutcomeStepDescriptor<{model.StateType}>(Id, ExecuteAsync)",
            StepMode.Custom =>
                $"new global::Tandem.GeneratedCustomStepDescriptor<{model.StateType}, {resultType}>(Id, ExecuteAsync, AdaptResult)",
            _ =>
                $"new global::Tandem.GeneratedPipelineStepDescriptor<{model.StateType}, {resultType}>(Id, ExecuteAsync, AdaptResult)",
        };
        var adaptation = model.Mode is StepMode.Custom or StepMode.LegacyCustom
            ? RenderAdaptation(model, resultType)
            : string.Empty;

        return $$"""
            // <auto-generated />
            #nullable enable

            namespace {{model.Namespace}};

            {{model.Accessibility}} sealed partial class {{model.Name}}
                : global::Tandem.IGeneratedPipelineStep<{{model.StateType}}, {{resultType}}>
            {
                public string Id => "{{model.Id}}";

                [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
                public global::Tandem.PipelineNodeDescriptor Descriptor => {{descriptor}};
            {{resultApi}}
            {{adaptation}}
            }
            """;
    }

    private static string RenderCustomApi(StepModel model, string resultType)
    {
        var cases = string.Join(
            "\n",
            model.Cases.Select(resultCase =>
                $"            public global::Tandem.ResultCase<{model.StateType}, {resultType}, {resultType}.{resultCase.Name}> {resultCase.Name} => new(step, \"{resultCase.Name}\");"
            )
        );
        return $$"""

                public ResultRoutes Result => new(this);

                public readonly struct ResultRoutes({{model.Name}} step)
                {
            {{cases}}
                }
            """;
    }

    private static string RenderAdaptation(StepModel model, string resultType)
    {
        var cases = string.Join(
            "\n",
            model.Cases.Select(resultCase =>
                $$"""
                                {{resultType}}.{{resultCase.Name}} value => pipeline with
                                {
                                    State = value.State,
                                    LatestResult = global::Tandem.PipelineResultPayload.Create(Id, "{{resultCase.Name}}", value),
                                },
                    """
            )
        );
        return $$"""

                private global::Tandem.Domain.PipelineMessage<{{model.StateType}}> AdaptResult(
                    global::Tandem.Domain.PipelineMessage<{{model.StateType}}> pipeline,
                    {{resultType}} result
                ) => result switch
                {
            {{cases}}
                    _ => throw new global::System.InvalidOperationException("Unknown result case."),
                };
            """;
    }

    private enum StepMode
    {
        PassThrough,
        State,
        Outcome,
        Custom,
        LegacyCustom,
    }

    private sealed class StepModel
    {
        public StepModel(
            string @namespace,
            string accessibility,
            string name,
            string id,
            string stateType,
            string? resultType,
            StepMode mode,
            ImmutableArray<CaseModel> cases
        )
        {
            Namespace = @namespace;
            Accessibility = accessibility;
            Name = name;
            Id = id;
            StateType = stateType;
            ResultType = resultType;
            Mode = mode;
            Cases = cases;
        }

        public string Namespace { get; }
        public string Accessibility { get; }
        public string Name { get; }
        public string Id { get; }
        public string StateType { get; }
        public string? ResultType { get; }
        public StepMode Mode { get; }
        public ImmutableArray<CaseModel> Cases { get; }
    }

    private sealed class CaseModel
    {
        public CaseModel(string name) => Name = name;

        public string Name { get; }
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
}
