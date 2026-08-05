using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Domain;

public sealed record PlannerDecision(
    PlannerDecisionValue Decision,
    string Rationale,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> EvidenceUsed,
    string? HumanQuestion = null
);

public enum PlannerDecisionValue
{
    Proceed,
    ProceedWithConstraints,
    NeedsHuman,
    Stop,
}

public sealed record VerificationResult(
    int Index,
    string Command,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed
);

public sealed class VerificationResultJsonConverter : JsonConverter<VerificationResult>
{
    public override VerificationResult Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        return new VerificationResult(
            root.GetProperty("index").GetInt32(),
            root.GetProperty("command").GetString() ?? "",
            root.GetProperty("exitCode").GetInt32(),
            root.GetProperty("stdout").GetString() ?? "",
            root.GetProperty("stderr").GetString() ?? "",
            TimeSpan.FromMilliseconds(root.GetProperty("elapsedMs").GetDouble())
        );
    }

    public override void Write(
        Utf8JsonWriter writer,
        VerificationResult value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", value.Index);
        writer.WriteString("command", value.Command);
        writer.WriteNumber("exitCode", value.ExitCode);
        writer.WriteString("stdout", value.Stdout);
        writer.WriteString("stderr", value.Stderr);
        writer.WriteNumber("elapsedMs", value.Elapsed.TotalMilliseconds);
        writer.WriteEndObject();
    }
}

public sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => TimeSpan.FromMilliseconds(reader.GetDouble());

    public override void Write(
        Utf8JsonWriter writer,
        TimeSpan value,
        JsonSerializerOptions options
    ) => writer.WriteNumberValue(value.TotalMilliseconds);
}
