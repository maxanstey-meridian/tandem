using System.Text.Json;
using Json.Schema;
using Json.Schema.Generation;
using Json.Schema.Generation.Intents;
using Microsoft.Extensions.AI;

namespace Tandem;

internal static class StructuredOutputSchema
{
    private static readonly SchemaGeneratorConfiguration _configuration = new()
    {
        PropertyNameResolver = PropertyNameResolvers.CamelCase,
        Refiners = { new StrictObjectRefiner() },
    };
    private static readonly AIJsonSchemaTransformCache _transformer = new(
        new AIJsonSchemaTransformOptions
        {
            DisallowAdditionalProperties = true,
            RequireAllProperties = true,
            MoveDefaultKeywordToDescription = true,
        }
    );

    public static ChatResponseFormat Create<T>()
    {
        var schema = new JsonSchemaBuilder().FromType<T>(_configuration).Build();
        var format = ChatResponseFormat.ForJsonSchema(
            JsonSerializer.SerializeToElement(schema),
            typeof(T).Name
        );
        return ChatResponseFormat.ForJsonSchema(
            _transformer.GetOrCreateTransformedSchema((ChatResponseFormatJson)format)
                ?? throw new InvalidOperationException(
                    $"Could not transform the {typeof(T).Name} response schema."
                ),
            typeof(T).Name
        );
    }

    private sealed class StrictObjectRefiner : ISchemaRefiner
    {
        public bool ShouldRun(SchemaGenerationContextBase context) =>
            context.Intents.Any(intent => intent is PropertiesIntent);

        public void Run(SchemaGenerationContextBase context)
        {
            var properties = context.Intents.OfType<PropertiesIntent>().Single().Properties;
            context.Intents.Add(new StrictObjectIntent());
            context.Intents.RemoveAll(intent => intent is RequiredIntent);
            context.Intents.Add(new RequiredIntent(properties.Keys.ToList()));
        }
    }

    private sealed class StrictObjectIntent : ISchemaKeywordIntent
    {
        public void Apply(JsonSchemaBuilder builder) => builder.AdditionalProperties(false);
    }
}
