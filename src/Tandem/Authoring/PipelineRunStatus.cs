using System.Text.Json.Serialization;

namespace Tandem;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineRunStatus
{
    Succeeded,
    Failed,
}
