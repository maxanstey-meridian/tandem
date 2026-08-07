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
        "Pipeline stage '{0}' must be a partial class with one ExecuteAsync method accepting TState and CancellationToken and returning ValueTask, ValueTask<TState>, or ValueTask<Outcome<TState>>",
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
        var stateSymbol = method.Parameters[0].Type;
        var mode = StepMode.PassThrough;
        ITypeSymbol? resultSymbol = null;

        if (SymbolEqualityComparer.Default.Equals(method.ReturnType, valueTaskType)) { }
        else if (
            method.ReturnType is INamedTypeSymbol returnType
            && SymbolEqualityComparer.Default.Equals(
                returnType.OriginalDefinition,
                genericValueTaskType
            )
        )
        {
            resultSymbol = returnType.TypeArguments[0];
            if (SymbolEqualityComparer.Default.Equals(resultSymbol, stateSymbol))
            {
                mode = StepMode.State;
            }
            else if (
                resultSymbol is INamedTypeSymbol outcome
                && SymbolEqualityComparer.Default.Equals(outcome.OriginalDefinition, outcomeType)
                && SymbolEqualityComparer.Default.Equals(outcome.TypeArguments[0], stateSymbol)
            )
            {
                mode = StepMode.Outcome;
            }
            else
            {
                return Invalid(syntax, step.Name);
            }
        }
        else
        {
            return Invalid(syntax, step.Name);
        }

        return StepGenerationResult.Success(
            new StepModel(
                step.ContainingNamespace.ToDisplayString(),
                step.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
                step.Name,
                id!,
                stateSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                mode
            )
        );
    }

    private static StepGenerationResult Invalid(ClassDeclarationSyntax syntax, string name) =>
        StepGenerationResult.Failure(
            Diagnostic.Create(InvalidDeclaration, syntax.Identifier.GetLocation(), name)
        );

    private static string Render(StepModel model)
    {
        var resultType =
            model.Mode == StepMode.Outcome
                ? $"global::Tandem.Domain.Outcome<{model.StateType}>"
                : "global::Tandem.GeneratedStepCompletion";
        var resultApi = model.Mode switch
        {
            StepMode.Outcome => $$"""

                    public global::Tandem.PipelineOutcomeSelector<{{model.StateType}}> Success => new(this, failed: false);
                    public global::Tandem.PipelineOutcomeSelector<{{model.StateType}}> Failed => new(this, failed: true);
                """,
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
            _ => throw new System.InvalidOperationException(),
        };
        var interfaceType =
            model.Mode == StepMode.Outcome
                ? $"global::Tandem.IStandardOutcomePipelineStep<{model.StateType}>"
                : $"global::Tandem.IGeneratedPipelineStep<{model.StateType}, {resultType}>";

        return $$"""
            // <auto-generated />
            #nullable enable

            namespace {{model.Namespace}};

            {{model.Accessibility}} sealed partial class {{model.Name}}
                : {{interfaceType}}
            {
                public string Id => "{{model.Id}}";

                [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
                public global::Tandem.PipelineNodeDescriptor Descriptor => {{descriptor}};
            {{resultApi}}
            }
            """;
    }

    private enum StepMode
    {
        PassThrough,
        State,
        Outcome,
    }

    private sealed class StepModel
    {
        public StepModel(
            string @namespace,
            string accessibility,
            string name,
            string id,
            string stateType,
            StepMode mode
        )
        {
            Namespace = @namespace;
            Accessibility = accessibility;
            Name = name;
            Id = id;
            StateType = stateType;
            Mode = mode;
        }

        public string Namespace { get; }
        public string Accessibility { get; }
        public string Name { get; }
        public string Id { get; }
        public string StateType { get; }
        public StepMode Mode { get; }
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
