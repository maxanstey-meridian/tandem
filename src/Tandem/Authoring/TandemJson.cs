using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tandem;

/// <summary>
/// The single canonical <see cref="JsonSerializerOptions"/> for every
/// Tandem-generated typed JSON contract: capability request schemas, capability
/// argument deserialization, capability payload serialization, structured-output
/// schemas, structured-output deserialization, accepted-output portable payloads,
/// interaction schemas where typed values cross JSON, and TypeScript bridge
/// projections. Applications declare ordinary types with no attributes, converters,
/// schema overrides, or serializer settings; Tandem owns the JSON boundary and
/// guarantees: C# enum -> named JSON schema -> named model argument -> strict named
/// deserialization -> typed request.
/// </summary>
public static class TandemJson
{
    private static readonly JsonSerializerOptions _typedContract = Create();

    /// <summary>
    /// Creates options for the canonical contract used by typed JSON that crosses a Tandem boundary.
    /// Uses web defaults (camelCase, case-insensitive), disallows unmapped members,
    /// and serializes enums as named strings while rejecting integer enum inputs.
    /// </summary>
    public static JsonSerializerOptions CreateTypedContract() => new(_typedContract);

    internal static JsonSerializerOptions TypedContract => _typedContract;

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));

        return options;
    }
}
