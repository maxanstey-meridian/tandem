using Examples.CodeWriter;
using Microsoft.Extensions.AI;
using Tandem;

var submitImplementation = AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
    new SubmitImplementationCapability(),
    (state, submission) => state.RecordImplementation(submission)
);
var implementationResponse = new ChatResponse(
    new ChatMessage(
        ChatRole.Assistant,
        [
            new FunctionCallContent(
                "implementation-1",
                "submit_implementation",
                new Dictionary<string, object?>
                {
                    ["implementation"] =
                        "function (input) { return input.normalize(\"NFD\").replace(/[\\u0300-\\u036f]/g, \"\").toLowerCase().replace(/[^a-z0-9]+/g, \"-\").replace(/^-+|-+$/g, \"\"); }",
                    ["rationale"] = "Normalize diacritics, collapse separators, and trim the slug.",
                }
            ),
        ]
    )
)
{
    FinishReason = ChatFinishReason.ToolCalls,
    ModelId = "package-proof",
};
var participants = CodeWriterDefinitions.Create(
    new CodeWriterClients(
        new ScriptedChatClient(implementationResponse),
        new ScriptedChatClient(
            ScriptedChatClient.Text(
                "{\"decision\":\"Accept\",\"summary\":\"Implementation verified and accepted.\",\"findings\":[]}"
            )
        )
    ),
    submitImplementation
);
var result = await new PipelineRunner().RunAsync(
    new CodeWriterComposition(participants).Build(),
    new CodeWriterState([
        "Return a URL slug for the input string.",
        "Remove diacritics and non-alphanumeric separators.",
        "Return lowercase words joined by single hyphens.",
    ]),
    new PipelineRunOptions(Observer: new PackagePersistenceObserver()),
    CancellationToken.None
);
if (
    !result.Succeeded
    || result.State.Implementation is null
    || result.State.Verification?.Passed is not true
    || result.State.Review?.Decision != ReviewDisposition.Accept
)
{
    throw new Exception("CodeWriter package proof failed.");
}

var approval = PipelineNodes.WaitFor<CodeWriterState, string, string>(
    "approval",
    _ => "Approve the implementation.",
    (state, _) => state
);
var directComplete = PipelineNodes.Complete(new DirectCompletion());
var direct = Pipeline
    .Start(approval, "direct-interaction")
    .Persist(approval)
    .DoNotPersist(approval)
    .Route(approval, directComplete, "answered")
    .Build(directComplete);
var handlers = new PipelineInteractionHandlers().Handle(
    approval,
    (_, _) => ValueTask.FromResult("Approved")
);
var directResult = await new PipelineRunner().RunAsync(
    direct,
    new CodeWriterState(["Approve this code."]),
    new PipelineRunOptions(Interactions: handlers),
    CancellationToken.None
);
if (!directResult.Succeeded)
    throw new Exception("Interaction package proof failed.");

sealed class PackagePersistenceObserver : IPipelinePersistenceObserver
{
    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;
}

sealed class DirectCompletion : IPipelineCompletion<CodeWriterState>
{
    public string Id => "direct-complete";

    public string Summarize(CodeWriterState state) => "Direct interaction complete";
}
