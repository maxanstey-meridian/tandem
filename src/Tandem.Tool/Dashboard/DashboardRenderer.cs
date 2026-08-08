using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using Tandem.Delivery;
using Tandem.Domain;

namespace Tandem.Infrastructure.Dashboard;

public sealed class DashboardRenderer(IAnsiConsole? console = null)
{
    private const int NarrowWidth = 100;
    private const int MaxScrollbackLines = 2_000;
    private static readonly string[] _blockBackgrounds =
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
    private readonly IAnsiConsole _console = console ?? AnsiConsole.Console;
    private int _lastWidth;
    private int _lastHeight;
    private int _scrollOffset;
    private int _maxScrollOffset;
    private int _lastRenderedLineCount;
    private int _lastTranscriptCount;
    private int _viewportHeight = 1;

    public int Width => _console.Profile.Width;

    public int Height => _console.Profile.Height;

    public void RunInAlternateScreen(Action action) => _console.AlternateScreen(action);

    public void ScrollLines(int lines) =>
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, _maxScrollOffset);

    public void ScrollPage(int pages) => ScrollLines(pages * _viewportHeight);

    public void ScrollHome() => _scrollOffset = _maxScrollOffset;

    public void ScrollEnd() => _scrollOffset = 0;

    public void Render(DashboardModel model)
    {
        var width = Math.Max(40, Width);
        var height = Math.Max(12, Height);
        var headerHeight = 3;
        var footerHeight = 2;
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
                .Update(RenderPipeline(model, Math.Max(3, bodyHeight - workHeight)));
        }
        else
        {
            root["body"].SplitColumns(new Layout("work").Ratio(3), new Layout("pipeline").Ratio(1));
            root["body"]
                ["work"]
                .Update(RenderWork(model, bodyHeight, Math.Max(10, width * 3 / 4 - 4)));
            root["body"]["pipeline"].Update(RenderPipeline(model, bodyHeight));
        }
        root["footer"].Update(RenderFooter(model));

        if (_lastWidth != Width || _lastHeight != Height)
        {
            _console.Clear();
            _lastWidth = Width;
            _lastHeight = Height;
        }
        _console.Cursor.SetPosition(0, 0);
        _console.Write(root);
    }

    private static IRenderable RenderHeader(DashboardModel model)
    {
        var elapsed = model.StartedAt.HasValue
            ? (model.CompletedAt ?? DateTimeOffset.UtcNow) - model.StartedAt.Value
            : TimeSpan.Zero;
        var active = model.ActiveBlockId ?? (model.IsTerminal ? "complete" : "waiting");
        var runId = string.IsNullOrEmpty(model.RunId) ? "starting" : model.RunId;
        var text = new Text(
            $"{runId}  {model.Status}  {active}  {model.Model ?? ""}  {elapsed:hh\\:mm\\:ss}",
            StatusStyle(model.Status)
        ).Overflow(Overflow.Ellipsis);

        return new Panel(text).Border(BoxBorder.Rounded).Padding(1, 0, 1, 0);
    }

    private IRenderable RenderWork(DashboardModel model, int paneHeight, int paneWidth)
    {
        var activeBlockId = model.ActiveBlockId;
        var visibleCount = Math.Max(1, paneHeight - 2);
        _viewportHeight = visibleCount;
        var lines = new List<IRenderable>();
        var blockWidth = Math.Max(
            1,
            model.Transcript.Select(entry => entry.BlockId.Length).DefaultIfEmpty(1).Max()
        );

        for (
            var index = model.Transcript.Count - 1;
            index >= 0 && lines.Count < MaxScrollbackLines;
            index--
        )
        {
            var entry = model.Transcript[index];
            if (entry.Line is { Kind: EventKinds.ToolCompleted, ToolSuccess: true })
            {
                continue;
            }

            var rendered = RenderLines(entry.Line, entry.BlockId, blockWidth, paneWidth).ToList();
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

        var active =
            model.Blocks.FirstOrDefault(b => b.BlockId == activeBlockId)
            ?? model.Blocks.LastOrDefault();
        var title = active?.BlockId ?? "Work";
        var state =
            active?.IsActive == true ? "running"
            : model.Blocks.Count > 0 ? "done"
            : "waiting";
        return new Panel(new Rows(visibleLines))
            .Header($" {Markup.Escape(Center(title, blockWidth))} · {state} ")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IEnumerable<IRenderable> RenderLines(
        TranscriptLine line,
        string blockId,
        int blockWidth,
        int width
    )
    {
        var label = $"[{Center(blockId, blockWidth)}] ";
        var prefix = line.Kind switch
        {
            EventKinds.AgentReasoning => "· ",
            EventKinds.ToolStarted => "↯ ",
            EventKinds.ToolCompleted when line.ToolSuccess is true => "✓ ",
            EventKinds.ToolCompleted => "✗ ",
            _ => "  ",
        };
        var value = line.Kind switch
        {
            EventKinds.ToolStarted => line.Text,
            EventKinds.ToolCompleted when line.ToolSuccess is true => line.ToolName ?? line.Text,
            EventKinds.ToolCompleted => line.Text,
            _ => line.Text,
        };
        var background = BlockBackground(blockId);
        var jsonLines = TryRenderJson(value, label, prefix, background, width);
        if (jsonLines is not null)
        {
            foreach (var jsonLine in jsonLines)
            {
                yield return jsonLine;
            }
            yield break;
        }
        var contentColor = line.Kind switch
        {
            EventKinds.AgentReasoning => "grey",
            EventKinds.ToolStarted => "cornflowerblue",
            EventKinds.ToolCompleted when line.ToolSuccess is true => "green",
            EventKinds.ToolCompleted => "red",
            _ => null,
        };

        var coloredGutterWidth = label.Length + prefix.Length;
        var gutterWidth = coloredGutterWidth + 1;
        var availableWidth = Math.Max(10, width - gutterWidth);
        var first = true;
        foreach (var wrapped in WrapVisibleText(value, availableWidth))
        {
            var gutter = first ? label + prefix : new string(' ', coloredGutterWidth);
            var content = Markup.Escape(wrapped);
            if (contentColor is not null)
            {
                content = $"[{contentColor}]{content}[/]";
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
            var fragments = WrapVisibleText(lines[index], availableWidth).ToArray();
            if (fragments.Length == 0)
            {
                fragments = [""];
            }
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
                var color = probe < line.Length && line[probe] == ':' ? "cyan" : "green";
                AppendStyled(output, line[start..index], color);
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

    private static string BlockBackground(string blockId)
    {
        uint hash = 2166136261;
        foreach (var character in blockId)
        {
            hash ^= character;
            hash *= 16777619;
        }

        return _blockBackgrounds[(int)(hash % _blockBackgrounds.Length)];
    }

    private static string Center(string value, int width)
    {
        var padding = Math.Max(0, width - value.Length);
        var left = (padding + 1) / 2;
        return new string(' ', left) + value + new string(' ', padding - left);
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

    private static IRenderable RenderPipeline(DashboardModel model, int paneHeight)
    {
        var rows = new List<IRenderable>();
        foreach (var entry in model.PipelineHistory)
        {
            var duration =
                entry.Duration.TotalSeconds >= 1
                    ? $"{entry.Duration.TotalSeconds:F1}s"
                    : $"{entry.Duration.TotalMilliseconds:F0}ms";
            var icon = entry switch
            {
                { IsVerification: true, ExitCode: 0 } => "✓",
                { IsVerification: true } => "✗",
                { IsReview: true } => "✓",
                _ => "·",
            };
            rows.Add(
                new Text($"{icon} {entry.BlockId}  {entry.Kind}  {duration}").Overflow(
                    Overflow.Ellipsis
                )
            );
        }

        if (model.CandidateSha is { } sha)
        {
            rows.Add(new Text($"candidate  {sha[..Math.Min(12, sha.Length)]}"));
        }
        if (model.PublishedBranch is { } branch)
        {
            rows.Add(new Text($"published  {branch}", new Style(Color.Green)));
        }
        if (model.PendingHumanRequest is { } request)
        {
            rows.Add(
                new Text(
                    $"question [{request.SourceBlockId}] {request.Question}",
                    new Style(Color.Yellow)
                )
            );
            if (!string.IsNullOrWhiteSpace(request.Reason))
            {
                rows.Add(new Text($"reason  {request.Reason}", new Style(Color.Grey)));
            }
        }
        if (rows.Count == 0)
        {
            rows.Add(new Text("no pipeline history", new Style(Color.Grey)));
        }

        var visible = rows.TakeLast(Math.Max(1, paneHeight - 2));
        return new Panel(new Rows(visible)).Header(" Pipeline ").Border(BoxBorder.Rounded).Expand();
    }

    private IRenderable RenderFooter(DashboardModel model)
    {
        string text;
        Style style;
        if (model.PendingHumanRequest is not null)
        {
            text = $"> {model.DraftAnswer ?? ""}  Enter submit";
            style = new Style(Color.White);
        }
        else
        {
            var current = model.CurrentContextTokens ?? 0;
            var window = model.ContextWindowTokens ?? 0;
            var fraction = window > 0 ? Math.Clamp((double)current / window, 0, 1) : 0;
            const int barWidth = 20;
            var filled = (int)Math.Round(fraction * barWidth);
            var bar = new string('▓', filled) + new string('░', barWidth - filled);
            var usage = window > 0 ? $"{current / 1000.0:F1}k/{window / 1000.0:F0}k" : "—/—";
            var scroll = _scrollOffset > 0 ? $"↑ {_scrollOffset} lines · End follow  " : "";
            var keys = model.IsReady
                ? "↑↓/Pg scroll  p publish  q detach"
                : "↑↓/Pg scroll  q detach";
            text =
                $"{scroll}ctx {bar} {usage}  steps {model.PipelineHistory.Count}  {model.ActiveBlockId ?? model.Status.ToString()}  {keys}";
            style =
                fraction > 0.85 ? new Style(Color.Red)
                : fraction > 0.6 ? new Style(Color.Yellow)
                : new Style(Color.Grey);
        }

        return new Panel(new Text(text, style).Overflow(Overflow.Ellipsis))
            .Border(BoxBorder.None)
            .Padding(1, 0, 0, 0);
    }

    private static Style StatusStyle(RunStatus status) =>
        status switch
        {
            RunStatus.Ready => new Style(Color.Green, decoration: Decoration.Bold),
            RunStatus.Failed or RunStatus.Faulted => new Style(
                Color.Red,
                decoration: Decoration.Bold
            ),
            RunStatus.Cancelled => new Style(Color.Grey, decoration: Decoration.Bold),
            RunStatus.WaitingForHuman => new Style(Color.Yellow, decoration: Decoration.Bold),
            _ => new Style(Color.Cyan, decoration: Decoration.Bold),
        };
}
