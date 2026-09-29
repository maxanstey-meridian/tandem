using System.Text.Json;
using Examples.Debate;
using FluentValidation;
using Microsoft.Extensions.AI;
using Tandem;
using Tandem.Advanced;

var verdict = AgentCapabilities.Create<DebateState, SubmitVerdict>(
    new SubmitVerdictCapability(),
    (state, request) => state.RecordVerdict(request)
);
var judgeResponse = new ChatResponse(
    new ChatMessage(
        ChatRole.Assistant,
        [
            new FunctionCallContent(
                "verdict-1",
                "submit_verdict",
                new Dictionary<string, object?>
                {
                    ["verdict"] = "Affirmed",
                    ["reason"] = "Accepted.",
                }
            ),
        ]
    )
)
{
    FinishReason = ChatFinishReason.ToolCalls,
    ModelId = "package-proof",
};
var participants = DebateDefinitions.Create(
    new DebateOptions(
        new ScriptedChatClient(
            ScriptedChatClient.Text("{\"text\":\"Initial case\"}"),
            ScriptedChatClient.Text("{\"text\":\"Revised case\"}")
        ),
        new ScriptedChatClient(
            ScriptedChatClient.Text("{\"accepted\":false,\"critique\":\"Revise\"}"),
            ScriptedChatClient.Text("{\"accepted\":true,\"critique\":\"Accepted\"}")
        ),
        new ScriptedChatClient(judgeResponse)
    ),
    verdict
);
var result = await new PipelineRunner().RunAsync(
    new DebateComposition(participants).Build(),
    new DebateState("Should we proceed?", [], 0, null),
    cancellationToken: CancellationToken.None
);
if (!result.Succeeded || result.State.Verdict?.Value != "Affirmed")
    throw new Exception("Debate package proof failed.");

using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
var jsonOutput = Agent
    .Create<DebateState>(
        "json-output",
        "Return JSON.",
        new ScriptedChatClient(ScriptedChatClient.Text("{\"value\":1}"))
    )
    .WithMessage(_ => "Return a value.")
    .WithJsonOutput(
        new AgentJsonOutputDefinition<DebateState>(
            schema.RootElement,
            "Return a value.",
            _ => [],
            ValueType: "package.dynamic-output"
        ),
        (state, _) => state
    )
    .Build();
var outputResult = await new PipelineRunner().RunAsync(
    Pipeline.Start(jsonOutput, "json-output-proof").Build(jsonOutput),
    new DebateState("JSON output", [], 0, null)
);
if (!outputResult.Succeeded)
    throw new Exception("JSON output package proof failed.");

var jsonCapability = AgentCapabilities
    .CreateJson(
        new AgentJsonCapabilityDefinition<DebateState>(
            "accept_json",
            "Accept JSON.",
            schema.RootElement,
            _ => [],
            null,
            _ => "Accepted JSON.",
            "package.dynamic-capability"
        ),
        (state, _) => state
    )
    .WithAcceptance(
        (context, _) =>
        {
            IReadOnlyList<ToolInvocationObservation> invocations = context.ToolInvocations;
            if (invocations.Count != 0)
                throw new Exception("Unexpected prior package invocation.");
            return ValueTask.CompletedTask;
        }
    );
var capabilityResponse = new ChatResponse(
    new ChatMessage(
        ChatRole.Assistant,
        [
            new FunctionCallContent(
                "json-1",
                "accept_json",
                new Dictionary<string, object?> { ["value"] = 1 }
            ),
        ]
    )
)
{
    FinishReason = ChatFinishReason.ToolCalls,
    ModelId = "package-proof",
};
var jsonCapabilityAgent = Agent
    .Create<DebateState>(
        "json-capability",
        "Accept JSON.",
        new ScriptedChatClient(capabilityResponse)
    )
    .WithMessage(_ => "Accept a value.")
    .WithCapability(jsonCapability)
    .Build();
var capabilityResult = await new PipelineRunner().RunAsync(
    Pipeline.Start(jsonCapabilityAgent, "json-capability-proof").Build(jsonCapabilityAgent),
    new DebateState("JSON capability", [], 0, null)
);
if (!capabilityResult.Succeeded)
    throw new Exception("JSON capability package proof failed.");

var raw = Agent
    .Create<string>(
        "raw",
        "Return accepted.",
        new ScriptedChatClient(ScriptedChatClient.Text("accepted"))
    )
    .WithMessage(_ => "Decide.")
    .WithRawOutput(new RawWord(), (_, word) => word)
    .Build();
var rawResult = await new PipelineRunner().RunAsync(
    Pipeline.Start(raw, "raw-proof").Build(raw),
    "initial"
);
if (rawResult.State != "accepted")
    throw new Exception("Advanced raw output package proof failed.");

sealed class RawWord : IAgentRawOutputDefinition<string, string>
{
    public string Instructions => "Return accepted.";
    public IValidator<string> Validator { get; } = new InlineValidator<string>();

    public string Parse(string response) => response;
}
