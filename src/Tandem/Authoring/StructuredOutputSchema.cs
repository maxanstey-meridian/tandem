using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Tandem;

internal static class StructuredOutputSchema
{
    private static readonly AIJsonSchemaCreateOptions _inferenceOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            DisallowAdditionalProperties = true,
            RequireAllProperties = true,
            MoveDefaultKeywordToDescription = true,
        },
    };

    public static ChatResponseFormat Create<T>() =>
        ChatResponseFormat.ForJsonSchema(CreateJsonSchema<T>(), typeof(T).Name);

    public static JsonElement CreateJsonSchema<T>() =>
        AIJsonUtilities.CreateJsonSchema(
            typeof(T),
            serializerOptions: TandemJson.TypedContract,
            inferenceOptions: _inferenceOptions
        );
}
