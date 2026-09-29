using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

internal sealed partial class AgentBlock<TState>
{
    private PipelineMessage<TState> ResolveOutcome(
        AgentStructuredOutputResult<TState>? structuredResult,
        PipelineRuntime runtime,
        CapabilityInvocationState<TState> capabilityInvocation,
        bool requiresCheckpointRelease,
        bool policyExhausted,
        int continuationAttempt,
        PipelineRunContext? runContext
    )
    {
        PipelineMessage<TState> Outcome(
            TState state,
            string kind,
            string summary,
            JsonElement payload
        ) =>
            new(
                runtime.IncrementInvocations(config.StepId),
                state,
                new BlockOutcome(kind, config.StepId, summary, payload)
            )
            {
                RunContext = runContext,
            };

        var state = capabilityInvocation.State;
        if (capabilityInvocation.Accepted is { } accepted)
        {
            return ApplyAcceptedCapability(
                runtime,
                accepted,
                resetSession: requiresCheckpointRelease
                    && (config.Checkpoint?.ResetSessionAfterRelease ?? true),
                runContext
            );
        }

        if (requiresCheckpointRelease)
        {
            return Outcome(
                state,
                "agent.failed",
                $"Checkpoint-only mode: model did not call {config.Checkpoint!.Capability.ToolName}.",
                TandemJson.EmptyObject
            );
        }

        if (config.StructuredOutput is not null)
        {
            if (structuredResult is null)
            {
                throw new InvalidOperationException("Structured output was not evaluated.");
            }

            if (!structuredResult.Success)
            {
                return Outcome(
                    state,
                    "agent.failed",
                    "Structured output remained invalid after its corrective response.",
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            problems = structuredResult.Problems,
                            rawResponse = structuredResult.RawResponse,
                        }
                    )
                );
            }

            return Outcome(
                structuredResult.UpdatedState!,
                StandardOutcomeKinds.Success,
                "Succeeded",
                structuredResult.Payload
            );
        }

        return policyExhausted
            ? Outcome(
                state,
                "agent.failed",
                $"No lifecycle outcome after {continuationAttempt + 1} model turn(s).",
                JsonSerializer.SerializeToElement(
                    new { continuationAttempts = continuationAttempt }
                )
            )
            : Outcome(state, "agent.completed", "(no lifecycle call)", TandemJson.EmptyObject);
    }

    private PipelineMessage<TState> ApplyAcceptedCapability(
        PipelineRuntime runtime,
        AcceptedCapability<TState> accepted,
        bool resetSession,
        PipelineRunContext? runContext
    )
    {
        var updatedRuntime = runtime;
        foreach (
            var gate in (config.LatchedGates ?? []).Where(gate =>
                gate.ReleaseCapabilityId == accepted.CapabilityId
                && updatedRuntime.IsGateLatched(config.StepId, gate.Id)
            )
        )
        {
            updatedRuntime = updatedRuntime.WithStep(
                config.StepId,
                step => step with { Latches = step.Latches.Remove(gate.Id) }
            );
            if (gate.ResetSessionAfterRelease)
            {
                resetSession = true;
            }
        }
        if (resetSession)
        {
            updatedRuntime = updatedRuntime.WithStep(
                config.StepId,
                step => step with { Session = null, Usage = null }
            );
        }
        return new PipelineMessage<TState>(
            updatedRuntime.IncrementInvocations(config.StepId),
            accepted.State,
            new BlockOutcome(
                accepted.CapabilityId,
                config.StepId,
                accepted.Summary,
                accepted.Payload
            )
        )
        {
            RunContext = runContext,
        };
    }
}
