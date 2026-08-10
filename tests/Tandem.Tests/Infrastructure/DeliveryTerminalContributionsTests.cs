using System.Text.Json;
using FluentAssertions;
using Tandem.Ledger;
using Tandem.Terminal;
using Tandem.Tool;

namespace Tandem.Tests.Infrastructure;

public sealed class DeliveryTerminalContributionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tandem-terminal-{Guid.NewGuid():N}"
    );

    [Fact]
    public void DeliveryFormatsTypedHumanQuestionForTerminal()
    {
        var runId = Guid.CreateVersion7();
        var question = new HumanQuestion("Which behavior?", "A decision is required.");
        var observation = new PipelineInteractionRequested<HumanQuestion>(
            runId,
            "human",
            "request-1",
            question,
            JsonSerializer.SerializeToElement(question, JsonSerializerOptions.Web)
        );

        var prompt = DeliveryTerminalContributions.FormatInteraction(observation);

        prompt.Should().Be(new TerminalInteractionPrompt(question.Question, question.Reason));
    }

    [Fact]
    public async Task DeliveryEntriesProjectCandidateVerificationAndPublicationFromLedger()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteLedgerStore(Path.Combine(_directory, "ledger.sqlite3"));
        await store.InitializeAsync();
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var delivery = new DeliveryLedger(store.ForRun(runId));
        await delivery.AcceptPublicationCandidateAsync(
            "candidate",
            new PublicationCandidateDocument(
                "candidate",
                "repo",
                "/workspace",
                "packet",
                "base-sha",
                "candidate-sha"
            ),
            default
        );
        await delivery.AcceptVerificationResultAsync(
            "verification",
            new VerificationResult(
                0,
                "dotnet test",
                0,
                "passed",
                "",
                TimeSpan.FromSeconds(1),
                false
            ),
            default
        );
        var contributions = new DeliveryTerminalContributions(store, runId);

        var entries = await contributions.ReadAsync(default);

        entries.Should().Contain(new TerminalPipelineEntry("candidate", "candidate-sh", ""));
        entries
            .Should()
            .Contain(
                new TerminalPipelineEntry(
                    "verify",
                    "passed",
                    "dotnet test",
                    TimeSpan.FromSeconds(1),
                    TerminalPipelineEntryStyle.Success
                )
            );

        await store.CompleteRunAsync(runId, LedgerRunStatus.Ready);
        await delivery.AcceptPublicationResultAsync(
            new PublicationResultRecord("repo", "tandem/change", "candidate-sha", false),
            default
        );
        entries = await contributions.ReadAsync(default);
        entries
            .Should()
            .Contain(
                new TerminalPipelineEntry(
                    "published",
                    "tandem/change",
                    "",
                    Style: TerminalPipelineEntryStyle.Success
                )
            );
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
