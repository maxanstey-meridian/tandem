using Microsoft.Agents.AI;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

internal sealed partial class AgentBlock<TState>
{
    private bool IsCheckpointReleaseRequired(PipelineRuntime runtime)
    {
        return config.Checkpoint is not null
            && runtime.IsGateLatched(config.StepId, "checkpoint-required");
    }

    private PipelineRuntime LatchTriggeredGates(PipelineRuntime runtime, AgentUsage usage)
    {
        foreach (var gate in config.LatchedGates ?? [])
        {
            if (!runtime.IsGateLatched(config.StepId, gate.Id) && gate.Trigger(usage))
            {
                runtime = runtime.WithStep(
                    config.StepId,
                    step => step with { Latches = step.Latches.Add(gate.Id) }
                );
            }
        }
        return runtime;
    }

    private AgentUsage ResolveUsage(
        long? inputTokens,
        long? outputTokens,
        long cumulativeInputTokens,
        long cumulativeOutputTokens
    )
    {
        var policy = config.Checkpoint;
        var contextWindow =
            config.ContextBudget?.ContextWindowTokens ?? policy?.ContextWindowTokens ?? 0;

        var input = (int)(inputTokens ?? 0);
        var output = (int)(outputTokens ?? 0);
        var currentContext = input + output;

        return new AgentUsage(
            CurrentInputTokens: input,
            CurrentOutputTokens: output,
            CurrentContextTokens: currentContext,
            ContextWindowTokens: contextWindow,
            CumulativeInputTokens: cumulativeInputTokens,
            CumulativeOutputTokens: cumulativeOutputTokens
        );
    }

    private IReadOnlyList<ActiveAgentGate> ResolveActiveGates(PipelineMessage<TState> message)
    {
        var active = new List<ActiveAgentGate>();
        active.AddRange(
            (config.StateGuards ?? [])
                .Where(guard => guard.IsActive(message.State))
                .Select(guard => new ActiveAgentGate(
                    guard.Id,
                    guard.BlockedEffects,
                    guard.Message,
                    guard.RemediationCapabilityName
                ))
        );
        active.AddRange(
            (config.LatchedGates ?? [])
                .Where(gate => message.Runtime.IsGateLatched(config.StepId, gate.Id))
                .Select(gate => new ActiveAgentGate(
                    gate.Id,
                    gate.BlockedEffects,
                    gate.Message,
                    gate.ReleaseCapabilityName
                ))
        );
        return active;
    }

    private sealed record ActiveAgentGate(
        string Id,
        IReadOnlySet<ToolEffect> BlockedEffects,
        string Message,
        string? ReleaseCapabilityName
    );

    private PipelineMessage<TState> FinalizeConversation(PipelineMessage<TState> message)
    {
        if (config.RetainConversation is null || message.LatestOutcome is null)
        {
            return message;
        }
        if (config.RetainConversation(message, message.LatestOutcome))
        {
            return message;
        }

        return message with
        {
            Runtime = message.Runtime.WithStep(
                config.StepId,
                step => step.WithoutConversation() with { Profile = null }
            ),
        };
    }

    private PipelineRuntime ApplyPreInvocationPolicies(PipelineMessage<TState> message)
    {
        var runtime = message.Runtime;
        var profile =
            config.ProfilePolicy?.Invoke(message.State)
            ?? new AgentProfileSelection(config.ProfileName, "Configured agent profile.");
        return runtime.WithStep(
            config.StepId,
            step =>
                (
                    !config.ContinueSession
                    || (
                        step.Profile is { } current
                        && !string.Equals(
                            current.ProfileName,
                            profile.ProfileName,
                            StringComparison.Ordinal
                        )
                    )
                        ? step.WithoutConversation()
                        : step
                ) with
                {
                    Profile = profile,
                }
        );
    }

    private async Task<PipelineRuntime> CaptureSessionAsync(
        AIAgent agent,
        AgentSession session,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        return runtime.WithStep(config.StepId, step => step with { Session = serialized });
    }

    private PipelineRuntime CaptureToolInvocations(
        PipelineRuntime runtime,
        ToolOutcomeCollector collector
    ) =>
        runtime.WithStep(
            config.StepId,
            step => step with { ToolInvocations = collector.ToolInvocations }
        );

    private async Task<AgentSession> RestoreOrCreateSessionAsync(
        AIAgent agent,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        if (runtime.Step(config.StepId).Session is { } serialized)
        {
            return await agent.DeserializeSessionAsync(serialized, cancellationToken: ct);
        }

        return await agent.CreateSessionAsync(ct);
    }
}
