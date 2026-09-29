using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Json;
using Spectre.Console.Rendering;

namespace Tandem.Terminal;

internal sealed class TerminalRenderer(
    IAnsiConsole console,
    IReadOnlyList<TerminalKeyAction>? keyActions = null,
    IReadOnlyList<string>? pipelineLabels = null
)
{
    private const int NarrowWidth = 100;
    private const int MaxScrollbackLines = 2_000;
    private const int PipelineDurationWidth = 8;
    private const int PipelineResultWidth = 9;
    private static readonly JsonSerializerOptions _displayJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly Style _jsonPunctuation = new(Color.Grey);
    private static readonly string[] _stepBackgrounds =
    [
        "#244866",
        "#56375F",
        "#25563F",
        "#654124",
        "#61323A",
        "#205765",
        "#5D5220",
        "#343A68",
        "#61355C",
        "#465D28",
    ];
    private int _lastWidth;
    private int _lastHeight;
    private int _scrollOffset;
    private int _maxScrollOffset;
    private int _lastRenderedLineCount;
    private int _lastTranscriptCount;
    private int _viewportHeight = 1;
    private readonly int _pipelineContentWidth =
        (pipelineLabels ?? [])
            .Select(TerminalText.Sanitize)
            .DefaultIfEmpty("Pipeline")
            .Max(CellWidth)
        + PipelineDurationWidth
        + PipelineResultWidth
        + 12;

    public void ScrollLines(int lines) =>
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, _maxScrollOffset);

    public void ScrollPage(int pages) => ScrollLines(pages * _viewportHeight);

    public void ScrollHome() => _scrollOffset = _maxScrollOffset;

    public void ScrollEnd() => _scrollOffset = 0;

    public void Render(
        TerminalSnapshot model,
        IReadOnlyList<TerminalPipelineEntry>? pipelineEntries = null
    )
    {
        var width = Math.Max(40, console.Profile.Width);
        var height = Math.Max(12, console.Profile.Height);
        const int headerHeight = 3;
        const int footerHeight = 2;
        var bodyHeight = Math.Max(7, height - headerHeight - footerHeight);
        var root = new Layout("root").SplitRows(
            new Layout("header").Size(headerHeight),
            new Layout("body").Size(bodyHeight),
            new Layout("footer").Size(footerHeight)
        );

        root["header"].Update(RenderHeader(model));
        if (width < NarrowWidth)
        {
            var workHeight = Math.Max(4, bodyHeight * 4 / 5);
            root["body"]
                .SplitRows(
                    new Layout("work").Size(workHeight),
                    new Layout("pipeline").Size(Math.Max(3, bodyHeight - workHeight))
                );
            root["body"]["work"].Update(RenderWork(model, workHeight, Math.Max(10, width - 4)));
            root["body"]
                ["pipeline"]
                .Update(
                    RenderPipeline(
                        model,
                        Math.Max(3, bodyHeight - workHeight),
                        Math.Max(10, width - 2),
                        pipelineEntries
                    )
                );
        }
        else
        {
            var pipelineWidth = Math.Min(_pipelineContentWidth, width / 2);
            root["body"]
                .SplitColumns(new Layout("work"), new Layout("pipeline").Size(pipelineWidth));
            root["body"]
                ["work"]
                .Update(RenderWork(model, bodyHeight, Math.Max(10, width - pipelineWidth - 4)));
            root["body"]
                ["pipeline"]
                .Update(
                    RenderPipeline(
                        model,
                        bodyHeight,
                        Math.Max(10, pipelineWidth - 2),
                        pipelineEntries
                    )
                );
        }
        root["footer"].Update(RenderFooter(model));

        if (_lastWidth != console.Profile.Width || _lastHeight != console.Profile.Height)
        {
            console.Clear();
            _lastWidth = console.Profile.Width;
            _lastHeight = console.Profile.Height;
        }
        console.Cursor.SetPosition(0, 0);
        console.Write(root);
        console.Cursor.SetPosition(0, height - 1);
    }

    private static IRenderable RenderHeader(TerminalSnapshot model)
    {
        var output = new StringBuilder();
        AppendChrome(output, $"{model.RunId:N}", "cornflowerblue");
        if (!string.IsNullOrEmpty(model.Title))
        {
            AppendChrome(output, "  ", "grey");
            AppendChrome(output, TerminalText.Sanitize(model.Title), "mediumpurple1");
        }
        AppendChrome(output, "  ", "grey");
        AppendChrome(output, $"{model.Status}", StatusColor(model.Status), bold: true);
        AppendChrome(output, $"  {model.Elapsed:hh\\:mm\\:ss}", "grey");
        return new Panel(new Markup(output.ToString()).Overflow(Overflow.Ellipsis))
            .Border(BoxBorder.Rounded)
            .Padding(1, 0, 1, 0);
    }

    private IRenderable RenderWork(TerminalSnapshot model, int paneHeight, int paneWidth)
    {
        var visibleCount = Math.Max(1, paneHeight - 2);
        _viewportHeight = visibleCount;
        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        var lines = new List<SegmentLine>();
        var stepWidth = Math.Max(
            1,
            model.Transcript.Select(entry => CellWidth(entry.StepId)).DefaultIfEmpty(1).Max()
        );

        for (
            var index = model.Transcript.Count - 1;
            index >= 0 && lines.Count < MaxScrollbackLines;
            index--
        )
        {
            var entry = model.Transcript[index];
            if (entry is { Kind: TranscriptKind.ToolCompleted, Succeeded: true })
            {
                continue;
            }
            var rendered = RenderEntry(entry, stepWidth, paneWidth, options);
            lines.InsertRange(0, rendered.TakeLast(MaxScrollbackLines - lines.Count));
        }
        if (_scrollOffset > 0 && model.Transcript.Count > _lastTranscriptCount)
        {
            _scrollOffset += Math.Max(0, lines.Count - _lastRenderedLineCount);
        }
        _maxScrollOffset = Math.Max(0, lines.Count - visibleCount);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _maxScrollOffset);
        _lastRenderedLineCount = lines.Count;
        _lastTranscriptCount = model.Transcript.Count;

        var start = Math.Max(0, lines.Count - visibleCount - _scrollOffset);
        IRenderable content =
            lines.Count == 0
                ? new Text("waiting for activity…", new Style(Color.Grey))
                : new RenderedLines(lines.Skip(start).Take(visibleCount).ToList());
        var title = FormatWorkHeader(
            model.ModelName,
            model.CurrentContextTokens,
            model.ContextWindowTokens
        );
        return new Panel(content).Header($" {title} ").Border(BoxBorder.Rounded).Expand();
    }

    private static string FormatWorkHeader(
        string? modelName,
        long currentContextTokens,
        int? contextWindowTokens
    )
    {
        if (modelName is null)
        {
            return "";
        }

        var escaped = Markup.Escape(modelName);
        if (contextWindowTokens is { } tokens and > 0)
        {
            return $"{escaped} · ctx {FormatTokens(currentContextTokens)}/{FormatTokens(tokens)}";
        }

        return escaped;
    }

    private static string FormatTokens(long tokens) =>
        tokens < 1000 ? tokens.ToString() : $"{tokens / 1000}k";

    private static List<SegmentLine> RenderEntry(
        TranscriptEntry entry,
        int stepWidth,
        int width,
        RenderOptions options
    )
    {
        var label = $"[{entry.StepId}]" + new string(' ', stepWidth - CellWidth(entry.StepId) + 1);
        var prefix = entry.Kind switch
        {
            TranscriptKind.Reasoning => "· ",
            TranscriptKind.ToolStarted => "↯ ",
            TranscriptKind.ToolCompleted when entry.Succeeded is true => "✓ ",
            TranscriptKind.ToolCompleted => "✗ ",
            TranscriptKind.Action when entry.Succeeded is false => "✗ ",
            _ => "  ",
        };
        var gutter = label + prefix;
        var gutterWidth = CellWidth(gutter);
        var gutterStyle = new Style(Color.White, Color.FromHex(StepBackground(entry.StepId)));
        var contentWidth = Math.Max(10, width - gutterWidth - 1);
        var lines = Segment.SplitLines(
            EntryContent(entry, contentWidth).Render(options, contentWidth),
            contentWidth
        );
        for (var index = 0; index < lines.Count; index++)
        {
            lines[index].Prepend(Segment.Padding(1));
            lines[index]
                .Prepend(
                    new Segment(index == 0 ? gutter : new string(' ', gutterWidth), gutterStyle)
                );
        }
        return lines;
    }

    private static IRenderable EntryContent(TranscriptEntry entry, int width)
    {
        var hasWorkingDirectory = !string.IsNullOrWhiteSpace(entry.WorkingDirectory);
        var value =
            entry.Kind == TranscriptKind.ToolStarted
                ? ToolStartFormatter.Format(
                    entry.ToolName ?? entry.Text,
                    entry.Text,
                    entry.WorkingDirectory
                )
                : entry.Text;
        if (JsonContent(value) is { } json)
        {
            return json;
        }
        if (entry.Kind == TranscriptKind.ToolStarted)
        {
            return new Markup(ToolStartFormatter.FormatMarkup(value, hasWorkingDirectory));
        }

        var style =
            entry.Kind == TranscriptKind.Reasoning ? new Style(Color.Grey)
            : entry.Succeeded is false ? new Style(Color.Red)
            : Style.Plain;
        return new Rows(
            value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.TrimEnd())
                .Where(line => line.Length > 0)
                .Select(line =>
                {
                    var content = line.TrimStart();
                    var indent = Math.Min(line.Length - content.Length, width - 1);
                    return new Padder(new Text(content, style), new Padding(indent, 0, 0, 0));
                })
        );
    }

    private static Rows? JsonContent(string value)
    {
        var candidate = value.Trim();
        if (
            candidate.StartsWith("```json", StringComparison.OrdinalIgnoreCase)
            && candidate.EndsWith("```", StringComparison.Ordinal)
            && candidate.Length > 10
        )
        {
            candidate = candidate[7..^3].Trim();
        }
        var start = candidate.IndexOfAny(['{', '[']);
        if (start < 0 || candidate[^1] is not ('}' or ']'))
        {
            return null;
        }
        var documents = JsonDocuments(candidate[start..]);
        if (documents is null)
        {
            return null;
        }

        var preamble = candidate[..start].TrimEnd();
        return new Rows([
            .. preamble.Length == 0 ? [] : new[] { new Text(preamble) },
            .. documents.Select(JsonBlock),
        ]);
    }

    private static IReadOnlyList<string>? JsonDocuments(string json)
    {
        var reader = new Utf8JsonReader(
            Encoding.UTF8.GetBytes(json),
            new JsonReaderOptions { AllowMultipleValues = true }
        );
        var documents = new List<string>();
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
                {
                    return null;
                }
                documents.Add(
                    JsonSerializer.Serialize(JsonElement.ParseValue(ref reader), _displayJson)
                );
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return documents;
    }

    private static JsonText JsonBlock(string json) =>
        new(json)
        {
            BracesStyle = _jsonPunctuation,
            BracketsStyle = _jsonPunctuation,
            ColonStyle = _jsonPunctuation,
            CommaStyle = _jsonPunctuation,
            NullStyle = _jsonPunctuation,
            MemberStyle = new Style(Color.Aqua),
            StringStyle = new Style(Color.Green),
            NumberStyle = new Style(Color.CornflowerBlue),
            BooleanStyle = new Style(Color.Yellow),
        };

    private static string StepBackground(string stepId)
    {
        uint hash = 2166136261;
        foreach (var character in stepId)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return _stepBackgrounds[(int)(hash % _stepBackgrounds.Length)];
    }

    private IRenderable RenderPipeline(
        TerminalSnapshot model,
        int paneHeight,
        int paneWidth,
        IReadOnlyList<TerminalPipelineEntry>? pipelineEntries
    )
    {
        var entries = PipelineEntries(model, pipelineEntries);
        var rows = entries.TakeLast(Math.Max(1, paneHeight - 2)).ToList();
        if (rows.Count == 0 && model.Interaction is null)
        {
            return new Panel(new Text("no pipeline history", new Style(Color.Grey)))
                .Header(" Pipeline ")
                .Border(BoxBorder.Rounded)
                .Expand();
        }

        const int durationWidth = PipelineDurationWidth;
        var labelWidth = Math.Min(
            rows.Select(entry => CellWidth(TerminalText.Sanitize(entry.Label)))
                .DefaultIfEmpty(1)
                .Max(),
            Math.Max(1, paneWidth - durationWidth - 12)
        );
        var resultWidth = Math.Max(1, paneWidth - labelWidth - durationWidth - 10);
        var grid = new Grid { Expand = true };
        grid.AddColumn(
            new GridColumn
            {
                Width = 1,
                NoWrap = true,
                Padding = new Padding(0, 0),
            }
        );
        grid.AddColumn(
            new GridColumn
            {
                Width = labelWidth,
                NoWrap = true,
                Padding = new Padding(1, 0, 0, 0),
            }
        );
        grid.AddColumn(
            new GridColumn
            {
                Width = durationWidth,
                Alignment = Justify.Right,
                NoWrap = true,
                Padding = new Padding(1, 0, 0, 0),
            }
        );
        grid.AddColumn(
            new GridColumn
            {
                Width = resultWidth,
                NoWrap = true,
                Padding = new Padding(1, 0, 0, 0),
            }
        );
        foreach (var entry in rows.TakeLast(Math.Max(1, paneHeight - 2)))
        {
            grid.AddRow(RenderPipelineEntry(entry, labelWidth, resultWidth));
        }
        var content = new List<IRenderable> { grid };
        if (model.Interaction is { } interaction)
        {
            content.Add(
                new Text(
                    TerminalText.Sanitize(interaction.Prompt),
                    new Style(Color.Yellow)
                ).Overflow(Overflow.Ellipsis)
            );
            if (!string.IsNullOrWhiteSpace(interaction.Detail))
            {
                content.Add(
                    new Text(
                        TerminalText.Sanitize(interaction.Detail),
                        new Style(Color.Grey)
                    ).Overflow(Overflow.Ellipsis)
                );
            }
        }
        return new Panel(new Rows(content)).Header(" Pipeline ").Border(BoxBorder.Rounded).Expand();
    }

    private static IReadOnlyList<TerminalPipelineEntry> PipelineEntries(
        TerminalSnapshot model,
        IReadOnlyList<TerminalPipelineEntry>? pipelineEntries
    ) =>
        model
            .Visits.Select(visit => new TerminalPipelineEntry(
                visit.StepId,
                visit.Outcome ?? "running",
                visit.Summary ?? "",
                visit.Duration,
                visit.Outcome switch
                {
                    StandardOutcomeKinds.Success => TerminalPipelineEntryStyle.Success,
                    "faulted" or "cancelled" => TerminalPipelineEntryStyle.Failure,
                    _ => TerminalPipelineEntryStyle.Information,
                }
            ))
            .Concat(pipelineEntries ?? [])
            .ToList();

    private static IRenderable[] RenderPipelineEntry(
        TerminalPipelineEntry entry,
        int labelWidth,
        int resultWidth
    )
    {
        var icon = entry.Style switch
        {
            TerminalPipelineEntryStyle.Success => "✓",
            TerminalPipelineEntryStyle.Failure => "✗",
            TerminalPipelineEntryStyle.Interaction => "?",
            _ => "·",
        };
        var color = entry.Style switch
        {
            TerminalPipelineEntryStyle.Success => Color.Green,
            TerminalPipelineEntryStyle.Failure => Color.Red,
            TerminalPipelineEntryStyle.Interaction => Color.Yellow,
            _ => Color.Default,
        };
        var result = PipelineResult(entry);
        return
        [
            new Text(icon, new Style(color)),
            new Text(Truncate(TerminalText.Sanitize(entry.Label), labelWidth)),
            new Text(
                Truncate(FormatDuration(entry.Duration), PipelineDurationWidth),
                new Style(Color.Grey)
            ),
            new Text(Truncate(result, resultWidth), new Style(color)),
        ];
    }

    private static string Truncate(string value, int width) =>
        Segment.SplitOverflow(new Segment(value), Overflow.Ellipsis, width)[0].Text;

    private static string FormatDuration(TimeSpan? duration) =>
        duration switch
        {
            { TotalSeconds: >= 1 } value => $"{value.TotalSeconds:F1}s",
            { } value => $"{value.TotalMilliseconds:F0}ms",
            _ => "",
        };

    private static string PipelineResult(TerminalPipelineEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Summary)
            ? HumanizePipelineKind(entry.Kind)
            : TerminalText.Sanitize(entry.Summary);

    private static string HumanizePipelineKind(string kind) =>
        kind switch
        {
            "running" => "Running",
            "waiting" => "Waiting",
            "faulted" => "Faulted",
            "cancelled" => "Cancelled",
            StandardOutcomeKinds.Success => "Succeeded",
            _ => TerminalText.Sanitize(kind),
        };

    private IRenderable RenderFooter(TerminalSnapshot model)
    {
        var output = new StringBuilder();
        if (model.Interaction is not null)
        {
            AppendChrome(output, "> ", "cornflowerblue");
            AppendChrome(output, TerminalText.Sanitize(model.Draft), "white");
            AppendChrome(output, "  Enter", "yellow");
            AppendChrome(output, " submit", "grey");
        }
        else
        {
            if (_scrollOffset > 0)
            {
                AppendChrome(output, $"↑ {_scrollOffset} lines", "cornflowerblue");
                AppendChrome(output, " · ", "grey");
                AppendChrome(output, "End", "yellow");
                AppendChrome(output, " follow  ", "grey");
            }
            AppendChrome(output, "steps", "cyan");
            AppendChrome(output, $" {model.Visits.Count}", "white");
            foreach (
                var action in (keyActions ?? []).Where(action =>
                    action.IsAvailable?.Invoke() ?? true
                )
            )
            {
                AppendChrome(output, "  ", "grey");
                AppendChrome(output, action.Key.ToString().ToLowerInvariant(), "yellow");
                AppendChrome(output, $" {TerminalText.Sanitize(action.Label)}", "white");
            }
            AppendChrome(output, "  ", "grey");
            AppendChrome(output, "↑↓/Pg/Home/End", "yellow");
            AppendChrome(output, " scroll  ", "grey");
            AppendChrome(output, "q", "yellow");
            AppendChrome(
                output,
                IsTerminal(model.Status) ? " close" : " cancel",
                IsTerminal(model.Status) ? "grey" : "red"
            );
            if (!string.IsNullOrEmpty(model.WorkingDirectory))
            {
                AppendChrome(output, "  ", "grey");
                AppendChrome(
                    output,
                    TerminalText.Sanitize(model.WorkingDirectory),
                    "mediumpurple1"
                );
            }
        }
        return new Panel(new Markup(output.ToString()).Overflow(Overflow.Ellipsis))
            .Border(BoxBorder.None)
            .Padding(1, 0, 0, 0);
    }

    private static void AppendChrome(
        StringBuilder output,
        string value,
        string color,
        bool bold = false
    ) =>
        output
            .Append('[')
            .Append(color)
            .Append(bold ? " bold]" : "]")
            .Append(Markup.Escape(value))
            .Append("[/]");

    private static bool IsTerminal(TerminalPipelineStatus status) =>
        status
            is TerminalPipelineStatus.Succeeded
                or TerminalPipelineStatus.Failed
                or TerminalPipelineStatus.Faulted
                or TerminalPipelineStatus.Cancelled;

    private static string StatusColor(TerminalPipelineStatus status) =>
        status switch
        {
            TerminalPipelineStatus.Succeeded => "green",
            TerminalPipelineStatus.Failed or TerminalPipelineStatus.Faulted => "red",
            TerminalPipelineStatus.Cancelled => "grey",
            TerminalPipelineStatus.WaitingForInteraction => "yellow",
            _ => "cyan",
        };

    private static int CellWidth(string value) => new Segment(value).CellCount();

    private sealed class RenderedLines(IReadOnlyList<SegmentLine> lines) : Renderable
    {
        protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
            lines.SelectMany<SegmentLine, Segment>(
                (line, index) => index == 0 ? line : [Segment.LineBreak, .. line]
            );
    }
}
