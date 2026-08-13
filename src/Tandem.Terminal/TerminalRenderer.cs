using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Tandem.Terminal;

internal sealed class TerminalRenderer(
    IAnsiConsole console,
    IReadOnlyList<TerminalKeyAction>? keyActions = null
)
{
    private const int NarrowWidth = 100;
    private const int MaxScrollbackLines = 2_000;
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
            root["body"].SplitColumns(new Layout("work").Ratio(3), new Layout("pipeline").Ratio(1));
            root["body"]
                ["work"]
                .Update(RenderWork(model, bodyHeight, Math.Max(10, width * 3 / 4 - 4)));
            root["body"]
                ["pipeline"]
                .Update(
                    RenderPipeline(model, bodyHeight, Math.Max(10, width / 4 - 2), pipelineEntries)
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
        var elapsed = (model.CompletedAt ?? DateTimeOffset.UtcNow) - model.StartedAt;
        var text = new Text(
            $"{model.RunId:N}  {model.Status}  {elapsed:hh\\:mm\\:ss}",
            StatusStyle(model.Status)
        ).Overflow(Overflow.Ellipsis);
        return new Panel(text).Border(BoxBorder.Rounded).Padding(1, 0, 1, 0);
    }

    private IRenderable RenderWork(TerminalSnapshot model, int paneHeight, int paneWidth)
    {
        var visibleCount = Math.Max(1, paneHeight - 2);
        _viewportHeight = visibleCount;
        var lines = new List<IRenderable>();
        var stepWidth = Math.Max(
            1,
            model.Transcript.Select(entry => entry.StepId.Length).DefaultIfEmpty(1).Max()
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
            var rendered = RenderLines(entry, stepWidth, paneWidth).ToList();
            var remaining = MaxScrollbackLines - lines.Count;
            if (rendered.Count > remaining)
            {
                rendered = rendered.TakeLast(remaining).ToList();
            }
            lines.InsertRange(0, rendered);
        }
        if (lines.Count == 0)
        {
            lines.Add(new Text("waiting for activity…", new Style(Color.Grey)));
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
        var visibleLines = lines.Skip(start).Take(visibleCount).ToList();
        var title = model.ModelName is null ? "" : Markup.Escape(model.ModelName);
        return new Panel(new Rows(visibleLines))
            .Header($" {title} ")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IEnumerable<IRenderable> RenderLines(
        TranscriptEntry entry,
        int stepWidth,
        int width
    )
    {
        var label = $"[{entry.StepId}]".PadRight(stepWidth + 3);
        var prefix = entry.Kind switch
        {
            TranscriptKind.Reasoning => "· ",
            TranscriptKind.ToolStarted => "↯ ",
            TranscriptKind.ToolCompleted when entry.Succeeded is true => "✓ ",
            TranscriptKind.ToolCompleted => "✗ ",
            TranscriptKind.Action when entry.Succeeded is false => "✗ ",
            TranscriptKind.Semantic => "  ",
            _ => "  ",
        };
        var value =
            entry.Kind == TranscriptKind.ToolStarted ? entry.ToolName ?? entry.Text : entry.Text;
        var background = StepBackground(entry.StepId);
        var jsonLines = TryRenderJson(value, label, prefix, background, width);
        if (jsonLines is not null)
        {
            foreach (var line in jsonLines)
            {
                yield return line;
            }
            yield break;
        }

        var coloredGutterWidth = label.Length + prefix.Length;
        var availableWidth = Math.Max(10, width - coloredGutterWidth - 1);
        var first = true;
        foreach (var wrapped in WrapVisibleText(value, availableWidth))
        {
            var gutter = first ? label + prefix : new string(' ', coloredGutterWidth);
            var content = Markup.Escape(wrapped);
            if (entry.Kind == TranscriptKind.Reasoning)
            {
                content = $"[grey]{content}[/]";
            }
            else if (entry.Kind == TranscriptKind.ToolStarted)
            {
                content = $"[cornflowerblue]{content}[/]";
            }
            else if (entry.Succeeded is false)
            {
                content = $"[red]{content}[/]";
            }
            yield return new Markup(
                $"[white on {background}]{Markup.Escape(gutter)}[/] {content}"
            ).Overflow(Overflow.Ellipsis);
            first = false;
        }
    }

    private static IReadOnlyList<IRenderable>? TryRenderJson(
        string value,
        string label,
        string prefix,
        string background,
        int width
    )
    {
        var candidate = value.Trim();
        string? preamble = null;
        if (candidate.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            if (!candidate.EndsWith("```", StringComparison.Ordinal) || candidate.Length <= 10)
            {
                return null;
            }
            candidate = candidate[7..^3].Trim();
        }
        else
        {
            var objectStart = candidate.IndexOfAny(['{', '[']);
            if (objectStart > 0)
            {
                preamble = candidate[..objectStart].TrimEnd();
                candidate = candidate[objectStart..];
            }
        }
        if (
            candidate.Length < 2
            || candidate[0] is not ('{' or '[')
            || candidate[^1] is not ('}' or ']')
        )
        {
            return null;
        }

        IReadOnlyList<string> documents;
        try
        {
            documents = ExtractJsonDocuments(candidate);
        }
        catch (JsonException)
        {
            return null;
        }
        var formattedDocuments = new List<string>(documents.Count);
        foreach (var json in documents)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                formattedDocuments.Add(
                    JsonSerializer.Serialize(
                        document.RootElement,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        }
                    )
                );
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var formatted = string.Join('\n', formattedDocuments);
        var gutterWidth = label.Length + prefix.Length;
        var lines = preamble is null
            ? formatted.Split('\n')
            : [.. preamble.Split('\n'), .. formatted.Split('\n')];
        var jsonStartsAt = preamble?.Split('\n').Length ?? 0;
        var availableWidth = Math.Max(10, width - gutterWidth - 1);
        var rendered = new List<IRenderable>();
        for (var index = 0; index < lines.Length; index++)
        {
            var fragments = WrapVisibleText(lines[index], availableWidth)
                .DefaultIfEmpty("")
                .ToArray();
            for (var fragmentIndex = 0; fragmentIndex < fragments.Length; fragmentIndex++)
            {
                var firstVisualLine = rendered.Count == 0;
                var gutter = firstVisualLine ? label + prefix : new string(' ', gutterWidth);
                var markup = new StringBuilder();
                if (firstVisualLine)
                {
                    markup.Append($"[white on {background}]{Markup.Escape(gutter)}[/] ");
                }
                else
                {
                    markup.Append(Markup.Escape(gutter)).Append(' ');
                }
                var fragment = fragments[fragmentIndex];
                if (index < jsonStartsAt)
                {
                    markup.Append(Markup.Escape(fragment));
                }
                else if (fragmentIndex == 0)
                {
                    AppendJsonTokens(markup, fragment);
                }
                else
                {
                    AppendStyled(markup, fragment, JsonContinuationColor(lines[index]));
                }
                rendered.Add(new Markup(markup.ToString()).Overflow(Overflow.Ellipsis));
            }
        }
        return rendered;
    }

    private static IReadOnlyList<string> ExtractJsonDocuments(string candidate)
    {
        var documents = new List<string>();
        var offset = 0;
        while (offset < candidate.Length)
        {
            while (offset < candidate.Length && char.IsWhiteSpace(candidate[offset]))
            {
                offset++;
            }
            if (offset == candidate.Length)
            {
                break;
            }
            if (candidate[offset] is not ('{' or '['))
            {
                throw new JsonException("Unexpected content between JSON documents.");
            }
            var start = offset;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (; offset < candidate.Length; offset++)
            {
                var character = candidate[offset];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (character == '"')
                {
                    inString = true;
                }
                else if (character is '{' or '[')
                {
                    depth++;
                }
                else if (character is '}' or ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        offset++;
                        documents.Add(candidate[start..offset]);
                        break;
                    }
                }
            }
            if (depth != 0 || inString)
            {
                throw new JsonException("Incomplete JSON document.");
            }
        }
        if (documents.Count == 0)
        {
            throw new JsonException("No JSON documents found.");
        }
        return documents;
    }

    private static string JsonContinuationColor(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return "grey";
        }
        var value = line[(colon + 1)..].TrimStart();
        if (value.StartsWith('"'))
        {
            return "green";
        }
        if (
            value.StartsWith("true", StringComparison.Ordinal)
            || value.StartsWith("false", StringComparison.Ordinal)
        )
        {
            return "yellow";
        }
        return value.Length > 0 && (value[0] == '-' || char.IsDigit(value[0]))
            ? "cornflowerblue"
            : "grey";
    }

    private static void AppendJsonTokens(StringBuilder output, string line)
    {
        var index = 0;
        while (index < line.Length)
        {
            var start = index;
            var character = line[index];
            if (character == '"')
            {
                index++;
                var escaped = false;
                while (index < line.Length)
                {
                    var current = line[index++];
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        break;
                    }
                }
                var probe = index;
                while (probe < line.Length && char.IsWhiteSpace(line[probe]))
                {
                    probe++;
                }
                AppendStyled(
                    output,
                    line[start..index],
                    probe < line.Length && line[probe] == ':' ? "cyan" : "green"
                );
            }
            else if (character == '-' || char.IsDigit(character))
            {
                index++;
                while (
                    index < line.Length
                    && (char.IsDigit(line[index]) || line[index] is '.' or 'e' or 'E' or '+' or '-')
                )
                {
                    index++;
                }
                AppendStyled(output, line[start..index], "cornflowerblue");
            }
            else if (char.IsLetter(character))
            {
                index++;
                while (index < line.Length && char.IsLetter(line[index]))
                {
                    index++;
                }
                var token = line[start..index];
                AppendStyled(output, token, token is "true" or "false" ? "yellow" : "grey");
            }
            else
            {
                index++;
                AppendStyled(output, line[start..index], "grey");
            }
        }
    }

    private static void AppendStyled(StringBuilder output, string value, string color) =>
        output.Append('[').Append(color).Append(']').Append(Markup.Escape(value)).Append("[/]");

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

    private static IEnumerable<string> WrapVisibleText(string value, int width)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var rawLine in normalized.Split('\n'))
        {
            var content = rawLine.TrimEnd();
            if (content.Length == 0)
            {
                continue;
            }
            var indentLength = content.Length - content.TrimStart().Length;
            var indent = content[..Math.Min(indentLength, Math.Max(0, width - 1))];
            var remaining = content[indentLength..];
            var lineWidth = Math.Max(1, width - indent.Length);
            while (remaining.Length > lineWidth)
            {
                var breakAt = remaining.LastIndexOf(' ', lineWidth);
                if (breakAt <= 0)
                {
                    breakAt = lineWidth;
                }
                yield return indent + remaining[..breakAt].TrimEnd();
                remaining = remaining[breakAt..].TrimStart();
            }
            if (remaining.Length > 0)
            {
                yield return indent + remaining;
            }
        }
    }

    private IRenderable RenderPipeline(
        TerminalSnapshot model,
        int paneHeight,
        int paneWidth,
        IReadOnlyList<TerminalPipelineEntry>? pipelineEntries
    )
    {
        var entries = model
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
        var rows = entries.TakeLast(Math.Max(1, paneHeight - 2)).ToList();
        if (rows.Count == 0 && model.Interaction is null)
        {
            return new Panel(new Text("no pipeline history", new Style(Color.Grey)))
                .Header(" Pipeline ")
                .Border(BoxBorder.Rounded)
                .Expand();
        }

        var labelWidth = Math.Min(
            rows.Select(entry => TerminalText.Sanitize(entry.Label).Length).DefaultIfEmpty(1).Max(),
            Math.Max(1, paneWidth - 18)
        );
        var resultWidth = Math.Max(1, paneWidth - labelWidth - 16);
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
                Width = 6,
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

    private static IRenderable[] RenderPipelineEntry(
        TerminalPipelineEntry entry,
        int labelWidth,
        int resultWidth
    )
    {
        var duration = entry.Duration switch
        {
            { TotalSeconds: >= 1 } value => $"{value.TotalSeconds:F1}s",
            { } value => $"{value.TotalMilliseconds:F0}ms",
            _ => "",
        };
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
        var result = string.IsNullOrWhiteSpace(entry.Summary)
            ? HumanizePipelineKind(entry.Kind)
            : TerminalText.Sanitize(entry.Summary);
        return
        [
            new Text(icon, new Style(color)),
            new Text(Truncate(TerminalText.Sanitize(entry.Label), labelWidth)).Overflow(
                Overflow.Ellipsis
            ),
            new Text(duration, new Style(Color.Grey)),
            new Text(Truncate(result, resultWidth), new Style(color)).Overflow(Overflow.Ellipsis),
        ];
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

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
        string text;
        Style style;
        if (model.Interaction is { } interaction)
        {
            text = $"> {model.Draft}  Enter submit";
            style = new Style(Color.White);
        }
        else
        {
            var scroll = _scrollOffset > 0 ? $"↑ {_scrollOffset} lines · End follow  " : "";
            var actions = string.Join(
                "  ",
                (keyActions ?? [])
                    .Where(action => action.IsAvailable?.Invoke() ?? true)
                    .Select(action => $"{action.Key.ToString().ToLowerInvariant()} {action.Label}")
            );
            var quit = IsTerminal(model.Status) ? "q close" : "q cancel";
            text =
                $"{scroll}steps {model.Visits.Count}{(actions.Length == 0 ? "" : $"  {actions}")}  ↑↓/Pg/Home/End scroll  {quit}";
            style = new Style(Color.Grey);
        }
        return new Panel(new Text(TerminalText.Sanitize(text), style).Overflow(Overflow.Ellipsis))
            .Border(BoxBorder.None)
            .Padding(1, 0, 0, 0);
    }

    private static bool IsTerminal(TerminalPipelineStatus status) =>
        status
            is TerminalPipelineStatus.Succeeded
                or TerminalPipelineStatus.Failed
                or TerminalPipelineStatus.Faulted
                or TerminalPipelineStatus.Cancelled;

    private static Style StatusStyle(TerminalPipelineStatus status) =>
        status switch
        {
            TerminalPipelineStatus.Succeeded => new Style(Color.Green, decoration: Decoration.Bold),
            TerminalPipelineStatus.Failed or TerminalPipelineStatus.Faulted => new Style(
                Color.Red,
                decoration: Decoration.Bold
            ),
            TerminalPipelineStatus.Cancelled => new Style(Color.Grey, decoration: Decoration.Bold),
            TerminalPipelineStatus.WaitingForInteraction => new Style(
                Color.Yellow,
                decoration: Decoration.Bold
            ),
            _ => new Style(Color.Cyan, decoration: Decoration.Bold),
        };
}
