using System.Text.Json.Serialization;

namespace Tandem.Delivery;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Running,
    Ready,
    WaitingForHuman,
    Failed,
    Faulted,
    Cancelled,
}
