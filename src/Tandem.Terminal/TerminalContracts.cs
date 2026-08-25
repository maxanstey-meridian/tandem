using Spectre.Console;

namespace Tandem.Terminal;

public enum TerminalPipelineStatus
{
    Running,
    WaitingForInteraction,
    Succeeded,
    Failed,
    Faulted,
    Cancelled,
}

public sealed record TerminalCapabilities(bool IsInputTerminal, bool IsOutputTerminal)
{
    public bool IsInteractive => IsInputTerminal && IsOutputTerminal;

    public static TerminalCapabilities Detect() =>
        new(!Console.IsInputRedirected, !Console.IsOutputRedirected);
}

public interface ITerminalKeyInput
{
    public ValueTask<ConsoleKeyInfo?> ReadAsync(CancellationToken cancellationToken);
}

public sealed record TerminalInteractionPrompt(string Prompt, string? Detail = null);

public enum TerminalPipelineEntryStyle
{
    Information,
    Success,
    Failure,
    Interaction,
}

public sealed record TerminalPipelineEntry(
    string Label,
    string Kind,
    string Summary,
    TimeSpan? Duration = null,
    TerminalPipelineEntryStyle Style = TerminalPipelineEntryStyle.Information
);

public sealed record TerminalKeyAction(
    ConsoleKey Key,
    string Label,
    Func<CancellationToken, ValueTask> ExecuteAsync,
    Func<bool>? IsAvailable = null
);

public sealed record TerminalDisplayOptions
{
    public IAnsiConsole? Console { get; init; }

    public TerminalCapabilities Capabilities { get; init; } = TerminalCapabilities.Detect();

    public ITerminalKeyInput? KeyInput { get; init; }

    public Func<CancellationToken, ValueTask>? CancelAsync { get; init; }

    public Func<
        PipelineInteractionRequestedObservation,
        TerminalInteractionPrompt?
    >? FormatInteraction { get; init; }

    public Func<string, CancellationToken, ValueTask>? SubmitTextAsync { get; init; }

    public Func<bool>? CanSubmitText { get; init; }

    public Func<
        CancellationToken,
        ValueTask<IReadOnlyList<TerminalPipelineEntry>>
    >? ReadPipelineEntriesAsync { get; init; }

    public IReadOnlyList<TerminalKeyAction> KeyActions { get; init; } = [];

    public IReadOnlySet<string> TruncatedToolNames { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public int TranscriptEntryCapacity { get; init; } = 2_000;

    public int TranscriptCharacterCapacity { get; init; } = 200_000;

    public string? Title { get; init; }

    public string? WorkingDirectory { get; init; }
}
