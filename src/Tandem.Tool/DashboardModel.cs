using System.Collections.ObjectModel;
using System.Text.Json;
using Tandem.Delivery;

namespace Tandem.Domain;

public sealed record BlockTranscript(
    string BlockId,
    bool IsActive,
    bool IsCompleted,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? Duration,
    string? OutcomeKind,
    string? OutcomeSummary,
    IReadOnlyList<TranscriptLine> Lines
);

public sealed record TranscriptLine(
    string Kind,
    string Text,
    string? ToolName,
    bool? ToolSuccess,
    DateTimeOffset Timestamp
);

public sealed record TranscriptEntry(string BlockId, TranscriptLine Line);

public sealed record PipelineEntry(
    string BlockId,
    string Kind,
    string Summary,
    TimeSpan Duration,
    bool IsVerification,
    int? ExitCode,
    bool? IsReview,
    bool? IsHuman
);

public sealed record HumanRequestView(string SourceBlockId, string Question, string Reason);

public sealed record DashboardModel
{
    public string RunId { get; init; } = "";
    public string PacketPath { get; init; } = "";
    public string RepositoryPath { get; init; } = "";
    public string WorkspacePath { get; init; } = "";
    public RunStatus Status { get; init; } = RunStatus.Running;
    public string? ActiveBlockId { get; init; }
    public string? PinnedBaseSha { get; init; }
    public string? CandidateSha { get; init; }
    public string? PublishedBranch { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int? CurrentContextTokens { get; init; }
    public int? ContextWindowTokens { get; init; }
    public IReadOnlyList<BlockTranscript> Blocks { get; init; } = [];
    public IReadOnlyList<TranscriptEntry> Transcript { get; init; } = [];
    public IReadOnlyList<PipelineEntry> PipelineHistory { get; init; } = [];
    public HumanRequestView? PendingHumanRequest { get; init; }
    public string? DraftAnswer { get; init; }

    public bool IsReady => Status == RunStatus.Ready;
    public bool IsTerminal =>
        Status is RunStatus.Ready or RunStatus.Failed or RunStatus.Faulted or RunStatus.Cancelled;
}

public static class DashboardReducer
{
    public static DashboardModel Apply(DashboardModel model, RunEvent evt)
    {
        switch (evt.Kind)
        {
            case EventKinds.RunStarted:
            {
                var (runIdPart, packetPart) = ExtractRunStarted(evt.Message);
                return model with
                {
                    RunId = runIdPart,
                    PacketPath = packetPart,
                    Status = RunStatus.Running,
                    StartedAt = evt.Timestamp,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.RunReady:
            {
                var candidate = TryExtractCandidate(evt.Message);
                return model with
                {
                    Status = RunStatus.Ready,
                    CandidateSha = candidate ?? model.CandidateSha,
                    ActiveBlockId = null,
                    CompletedAt = evt.Timestamp,
                    Blocks = DeactivateBlocks(model.Blocks),
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.RunFailed:
                return model with
                {
                    Status = RunStatus.Failed,
                    ActiveBlockId = null,
                    CompletedAt = evt.Timestamp,
                    Blocks = DeactivateBlocks(model.Blocks),
                    UpdatedAt = evt.Timestamp,
                };

            case EventKinds.RunFaulted:
                return model with
                {
                    Status = RunStatus.Faulted,
                    ActiveBlockId = null,
                    CompletedAt = evt.Timestamp,
                    Blocks = DeactivateBlocks(model.Blocks),
                    UpdatedAt = evt.Timestamp,
                };

            case EventKinds.RunCancelled:
                return model with
                {
                    Status = RunStatus.Cancelled,
                    ActiveBlockId = null,
                    CompletedAt = evt.Timestamp,
                    Blocks = DeactivateBlocks(model.Blocks),
                    UpdatedAt = evt.Timestamp,
                };

            case EventKinds.RunPublished:
            {
                var branch = TryExtractPublishedBranch(evt.Message);
                return model with { PublishedBranch = branch, UpdatedAt = evt.Timestamp };
            }

            case EventKinds.BlockStarted:
            {
                var blocks = SetBlockActive(model.Blocks, evt.BlockId, evt.Timestamp);
                return model with
                {
                    ActiveBlockId = evt.BlockId,
                    Status = RunStatus.Running,
                    Blocks = blocks,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.BlockCompleted:
            {
                var (kind, summary) = ExtractOutcome(evt);
                var blocks = SetBlockCompleted(
                    model.Blocks,
                    evt.BlockId,
                    evt.Timestamp,
                    kind,
                    summary
                );
                var pipeline = AddPipelineEntry(
                    model.PipelineHistory,
                    evt.BlockId,
                    kind,
                    summary,
                    evt
                );
                return model with
                {
                    Blocks = blocks,
                    PipelineHistory = pipeline,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.AgentReasoning:
            case EventKinds.AgentText:
            {
                var (blocks, transcript) = AppendStreamingText(
                    model.Blocks,
                    model.Transcript,
                    evt.BlockId,
                    evt.Kind,
                    evt.Message,
                    evt.Timestamp
                );
                return model with
                {
                    Blocks = blocks,
                    Transcript = transcript,
                    ActiveBlockId = evt.BlockId,
                    Status = RunStatus.Running,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.ToolStarted:
            {
                var toolName = ExtractToolName(evt) ?? evt.Message;
                var (blocks, transcript) = AppendTranscriptLine(
                    model.Blocks,
                    model.Transcript,
                    evt.BlockId,
                    evt.Kind,
                    evt.Message,
                    evt.Timestamp,
                    toolName
                );
                return model with
                {
                    Blocks = blocks,
                    Transcript = transcript,
                    ActiveBlockId = evt.BlockId,
                    Status = RunStatus.Running,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.ToolCompleted:
            {
                var (success, toolName) = ExtractToolResult(evt);
                var (blocks, transcript) = AppendTranscriptLine(
                    model.Blocks,
                    model.Transcript,
                    evt.BlockId,
                    evt.Kind,
                    evt.Message,
                    evt.Timestamp,
                    toolName,
                    success
                );
                return model with
                {
                    Blocks = blocks,
                    Transcript = transcript,
                    ActiveBlockId = evt.BlockId,
                    Status = RunStatus.Running,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.CommandOutput:
            {
                var (blocks, transcript) = AppendTranscriptLine(
                    model.Blocks,
                    model.Transcript,
                    evt.BlockId,
                    evt.Kind,
                    evt.Message,
                    evt.Timestamp
                );
                return model with
                {
                    Blocks = blocks,
                    Transcript = transcript,
                    ActiveBlockId = evt.BlockId,
                    Status = RunStatus.Running,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.AgentUsage:
            {
                var (currentTokens, windowTokens, modelId) = ExtractUsage(evt);
                return model with
                {
                    CurrentContextTokens = currentTokens ?? model.CurrentContextTokens,
                    ContextWindowTokens = windowTokens ?? model.ContextWindowTokens,
                    Model = modelId ?? model.Model,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.HumanRequested:
            {
                var (sourceBlock, question, reason) = ExtractHumanRequest(evt);
                return model with
                {
                    PendingHumanRequest = new HumanRequestView(sourceBlock, question, reason),
                    Status = RunStatus.WaitingForHuman,
                    UpdatedAt = evt.Timestamp,
                };
            }

            case EventKinds.HumanAnswered:
                return model with
                {
                    PendingHumanRequest = null,
                    Status = RunStatus.Running,
                    DraftAnswer = null,
                    UpdatedAt = evt.Timestamp,
                };

            default:
                return model;
        }
    }

    public static DashboardModel FromEvents(
        IEnumerable<RunEvent> events,
        DashboardModel? seed = null
    )
    {
        var model = seed ?? new DashboardModel();
        foreach (var evt in events)
        {
            model = Apply(model, evt);
        }
        return model;
    }

    private static IReadOnlyList<BlockTranscript> SetBlockActive(
        IReadOnlyList<BlockTranscript> blocks,
        string blockId,
        DateTimeOffset ts
    )
    {
        var list = blocks
            .Select(block => block with { IsActive = block.BlockId == blockId })
            .ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].BlockId == blockId)
            {
                list[i] = list[i] with
                {
                    IsActive = true,
                    IsCompleted = false,
                    StartedAt = list[i].StartedAt ?? ts,
                    Lines = [],
                };
                return new ReadOnlyCollection<BlockTranscript>(list);
            }
        }
        list.Add(new BlockTranscript(blockId, true, false, ts, null, null, null, null, []));
        return new ReadOnlyCollection<BlockTranscript>(list);
    }

    private static IReadOnlyList<BlockTranscript> DeactivateBlocks(
        IReadOnlyList<BlockTranscript> blocks
    ) =>
        new ReadOnlyCollection<BlockTranscript>(
            blocks.Select(block => block with { IsActive = false }).ToList()
        );

    private static IReadOnlyList<BlockTranscript> SetBlockCompleted(
        IReadOnlyList<BlockTranscript> blocks,
        string blockId,
        DateTimeOffset ts,
        string? kind,
        string? summary
    )
    {
        var list = blocks.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].BlockId == blockId)
            {
                var startedAt = list[i].StartedAt;
                TimeSpan? duration = startedAt.HasValue ? ts - startedAt.Value : null;
                list[i] = list[i] with
                {
                    IsActive = false,
                    IsCompleted = true,
                    CompletedAt = ts,
                    Duration = duration,
                    OutcomeKind = kind,
                    OutcomeSummary = summary,
                };
                return new ReadOnlyCollection<BlockTranscript>(list);
            }
        }
        list.Add(new BlockTranscript(blockId, false, true, null, ts, null, kind, summary, []));
        return new ReadOnlyCollection<BlockTranscript>(list);
    }

    private static (
        IReadOnlyList<BlockTranscript> blocks,
        IReadOnlyList<TranscriptEntry> transcript
    ) AppendTranscriptLine(
        IReadOnlyList<BlockTranscript> blocks,
        IReadOnlyList<TranscriptEntry> transcript,
        string blockId,
        string kind,
        string text,
        DateTimeOffset ts,
        string? toolName = null,
        bool? toolSuccess = null
    )
    {
        var list = blocks
            .Select(block => block with { IsActive = block.BlockId == blockId })
            .ToList();
        var idx = list.FindIndex(b => b.BlockId == blockId);
        if (idx < 0)
        {
            list.Add(new BlockTranscript(blockId, true, false, ts, null, null, null, null, []));
            idx = list.Count - 1;
        }
        var line = new TranscriptLine(kind, text, toolName, toolSuccess, ts);
        list[idx] = list[idx] with
        {
            IsActive = true,
            IsCompleted = false,
            Lines = [.. list[idx].Lines, line],
        };
        var entry = new TranscriptEntry(blockId, line);
        var newTranscript = transcript.ToList();
        newTranscript.Add(entry);
        return (
            new ReadOnlyCollection<BlockTranscript>(list),
            new ReadOnlyCollection<TranscriptEntry>(newTranscript)
        );
    }

    private static (
        IReadOnlyList<BlockTranscript> blocks,
        IReadOnlyList<TranscriptEntry> transcript
    ) AppendStreamingText(
        IReadOnlyList<BlockTranscript> blocks,
        IReadOnlyList<TranscriptEntry> transcript,
        string blockId,
        string kind,
        string text,
        DateTimeOffset ts
    )
    {
        var list = blocks
            .Select(block => block with { IsActive = block.BlockId == blockId })
            .ToList();
        var idx = list.FindIndex(block => block.BlockId == blockId);
        if (idx < 0)
        {
            list.Add(new BlockTranscript(blockId, true, false, ts, null, null, null, null, []));
            idx = list.Count - 1;
        }

        var lines = list[idx].Lines.ToList();
        var newTranscript = transcript.ToList();

        var lastFlatEntryIsSameBlock =
            newTranscript.Count > 0 && newTranscript[^1].BlockId == blockId;
        var shouldCoalesce = lastFlatEntryIsSameBlock && lines.Count > 0 && lines[^1].Kind == kind;

        if (shouldCoalesce)
        {
            lines[^1] = lines[^1] with { Text = lines[^1].Text + text, Timestamp = ts };
            newTranscript[^1] = newTranscript[^1] with { Line = lines[^1] };
        }
        else if (!string.IsNullOrEmpty(text))
        {
            lines.Add(new TranscriptLine(kind, text, null, null, ts));
            newTranscript.Add(new TranscriptEntry(blockId, lines[^1]));
        }

        list[idx] = list[idx] with
        {
            IsActive = true,
            IsCompleted = false,
            Lines = new ReadOnlyCollection<TranscriptLine>(lines),
        };
        return (
            new ReadOnlyCollection<BlockTranscript>(list),
            new ReadOnlyCollection<TranscriptEntry>(newTranscript)
        );
    }

    private static IReadOnlyList<PipelineEntry> AddPipelineEntry(
        IReadOnlyList<PipelineEntry> history,
        string blockId,
        string? kind,
        string? summary,
        RunEvent evt
    )
    {
        var duration = ExtractDuration(evt);
        var isVerification = blockId.Contains("verif", StringComparison.OrdinalIgnoreCase);
        var isReview = blockId.Contains("review", StringComparison.OrdinalIgnoreCase);
        var isHuman = blockId.Contains("human", StringComparison.OrdinalIgnoreCase);
        int? exitCode = null;
        if (isVerification && evt.Data.HasValue)
        {
            try
            {
                exitCode = evt.Data.Value.GetProperty("exitCode").GetInt32();
            }
            catch
            {
                /* best-effort */
            }
        }
        var entry = new PipelineEntry(
            blockId,
            kind ?? "",
            summary ?? "",
            duration,
            isVerification,
            exitCode,
            isReview,
            isHuman
        );
        return new ReadOnlyCollection<PipelineEntry>([.. history, entry]);
    }

    private static (string? kind, string? summary) ExtractOutcome(RunEvent evt)
    {
        if (!evt.Data.HasValue)
        {
            return (null, null);
        }
        try
        {
            var data = evt.Data.Value;
            string? kind = data.TryGetProperty("kind", out var k) ? k.GetString() : null;
            string? summary = data.TryGetProperty("summary", out var s) ? s.GetString() : null;
            return (kind, summary);
        }
        catch
        {
            return (null, null);
        }
    }

    private static TimeSpan ExtractDuration(RunEvent evt)
    {
        if (!evt.Data.HasValue)
        {
            return TimeSpan.Zero;
        }
        try
        {
            if (evt.Data.Value.TryGetProperty("duration", out var d))
            {
                var ms = d.GetDouble();
                return TimeSpan.FromMilliseconds(ms);
            }
        }
        catch
        {
            /* best-effort */
        }
        return TimeSpan.Zero;
    }

    private static (bool success, string? toolName) ExtractToolResult(RunEvent evt)
    {
        bool? success = null;
        string? toolName = null;
        if (evt.Data.HasValue)
        {
            try
            {
                var data = evt.Data.Value;
                if (data.TryGetProperty("success", out var s))
                {
                    success = s.GetBoolean();
                }
                if (data.TryGetProperty("name", out var n))
                {
                    toolName = n.GetString();
                }
            }
            catch
            {
                /* best-effort */
            }
        }
        return (success ?? false, toolName);
    }

    private static string? ExtractToolName(RunEvent evt)
    {
        if (!evt.Data.HasValue)
        {
            return null;
        }

        try
        {
            var data = evt.Data.Value;
            return
                data.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int? currentTokens, int? windowTokens, string? model) ExtractUsage(RunEvent evt)
    {
        if (!evt.Data.HasValue)
        {
            return (null, null, null);
        }

        try
        {
            var data = evt.Data.Value;
            var input = data.TryGetProperty("inputTokens", out var inputValue)
                ? inputValue.GetInt64()
                : 0;
            var output = data.TryGetProperty("outputTokens", out var outputValue)
                ? outputValue.GetInt64()
                : 0;
            var current = checked((int)Math.Min(int.MaxValue, input + output));
            int? window =
                data.TryGetProperty("contextWindowTokens", out var windowValue)
                && windowValue.ValueKind == JsonValueKind.Number
                    ? windowValue.GetInt32()
                    : null;
            var model =
                data.TryGetProperty("model", out var modelValue)
                && modelValue.ValueKind == JsonValueKind.String
                    ? modelValue.GetString()
                    : null;
            return (current, window, model);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static (string sourceBlock, string question, string reason) ExtractHumanRequest(
        RunEvent evt
    )
    {
        if (evt.Data.HasValue)
        {
            try
            {
                var data = evt.Data.Value;
                var source = data.TryGetProperty("sourceBlockId", out var sb)
                    ? sb.GetString() ?? ""
                    : "";
                var question = data.TryGetProperty("question", out var q)
                    ? q.GetString() ?? ""
                    : "";
                var reason = data.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                return (source, question, reason);
            }
            catch
            {
                /* best-effort */
            }
        }
        return ("", evt.Message, "");
    }

    private static string? TryExtractCandidate(string message)
    {
        var idx = message.IndexOf("candidate:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }
        var rest = message[(idx + "candidate:".Length)..].Trim();
        if (rest.Equals("(none)", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var space = rest.IndexOf(' ');
        return space >= 0 ? rest[..space] : rest;
    }

    private static string? TryExtractPublishedBranch(string message)
    {
        var idx = message.IndexOf("Published:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }
        var rest = message[(idx + "Published:".Length)..].Trim();
        var newline = rest.IndexOf('\n');
        return newline >= 0 ? rest[..newline].Trim() : rest.Trim();
    }

    private static (string runId, string packetPath) ExtractRunStarted(string message)
    {
        var runIdx = message.IndexOf("Run ", StringComparison.OrdinalIgnoreCase);
        var startedIdx = message.IndexOf(" started from ", StringComparison.OrdinalIgnoreCase);
        if (runIdx < 0 || startedIdx < 0 || startedIdx <= runIdx + 4)
        {
            return ("", "");
        }

        var runId = message[(runIdx + 4)..startedIdx].Trim();
        var packet = message[(startedIdx + " started from ".Length)..].Trim();
        return (runId, packet);
    }
}
