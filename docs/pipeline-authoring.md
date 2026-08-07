# Pipeline Authoring

Tandem is an installable authoring and execution engine. A pipeline package owns
its durable state, steps, Dunet result unions, policies, lifecycle actions, and
route composition. Tandem generates step adapters and runs the resulting real
Microsoft Agent Framework workflow without exposing MAF to the pipeline package.

Delivery in [`src/Tandem.Delivery`](../src/Tandem.Delivery) is the complete
production example. [`samples/Tandem.Sample.Debate`](../samples/Tandem.Sample.Debate)
is the external-consumer proof: it references Tandem, has no Delivery dependency,
and imports no MAF namespaces.
[`samples/Tandem.Sample.Support`](../samples/Tandem.Sample.Support) proves a
materially different journey: agent classification and resolution, an injected
deterministic account lookup, and a typed durable customer-reply handoff with no
workspace or file tools.

## Author Journey

1. Reference `Tandem`, add `Tandem.Generators` as an analyzer, and reference
   `Dunet`. Use the [Support project](../samples/Tandem.Sample.Support/Tandem.Sample.Support.csproj)
   as the project-reference example until packages are published.
2. Define one immutable, serializable `<Name>State` containing durable lifecycle
   facts, never services, framework contexts, or a mutable state bag.
3. Implement each operation as a partial `<Name>Stage` or `<Name>Agent` marked
   with `[PipelineStage("stable-id")]`.
4. Give every step one nested `[Union]` result. Every case is a positional record
   whose first value is named `State` and has the pipeline state type.
5. Implement `ExecuteAsync(PipelineMessage<TState>, CancellationToken)` and return
   one authored result case. Undeclared failures are exceptions.
6. Supply prompts and an explicit pipeline-owned session policy for every agent.
   Profile and teardown policies are pipeline-owned when used.
7. Define validated `<Verb><Noun>Action` lifecycle actions and register the action
   set explicitly. Accepted actions remain receipt-backed, replay-safe, and
   conflict-detecting.
8. Put executable instances in one DI-constructed positional `<Name>Steps` record.
9. Declare the complete graph in `<Name>Composition`. Routes register real MAF
   edges immediately. Use generated `.Result.<Case>` selectors and explicit state
   predicates.
10. Add `<Name>Registration` for machinery, steps, inventory, composition, and
    lifecycle actions. Do not scan assemblies.
11. Test semantic inspection, state serialization, action validation/replay/
    conflict, in-process execution, and durable closed-generic execution.

## Agent Operations

`[PipelineStage]` makes a class executable and routable. It does not infer that a
class is model-backed. Configure model execution explicitly at the pipeline's
composition root with the DI-owned `AgentRuntime`:

```csharp
var classify = agentRuntime
    .Create<SupportState>(
        id: "classify",
        profile: "support",
        instructions: SupportPrompts.Classify,
        chatClient: supportClient)
    .WithMessage(pipeline => pipeline.State.Ticket)
    .WithStructuredOutput(SupportPolicies.ParseClassification)
    .WithSessionPolicy(SupportPolicies.StartFresh)
    .Build(context);
```

The four constructor arguments answer the basic operational questions directly:

- `id` is the stable durable identity and should match the authored step;
- `profile` names the model policy recorded for the invocation;
- `instructions` define the agent's role; and
- `chatClient` is the `Microsoft.Extensions.AI.IChatClient` supplied by the host.

`WithMessage` projects the latest typed pipeline message into the user message.
Every agent must select a session policy before `Build`; Tandem does not infer
whether a role should retain or reset context.

Optional capabilities are explicit:

```csharp
.WithWorkspace(...)
.WithStructuredOutput(...)
.WithLifecycleActions(...)
.WithCheckpoint(...)
.WithContinuationPolicy(...)
.WithMessageAugmentation(...)
.WithProfilePolicy(...)
.WithTeardownPolicy(...)
```

Workspace access is absent by default. Adding `WithWorkspace` enables MAF Harness
file tools for that agent; its mutation predicate decides whether write tools are
available for the current state. Non-file agents need no workspace property.

The authored agent receives `AgentOperation<TState>` and maps its semantic
outcome into the agent's Dunet result union:

```csharp
[PipelineStage("classify")]
public sealed partial class ClassifyAgent(AgentOperation<SupportState> operation)
{
    [Union]
    public partial record ClassifyResult
    {
        public partial record Categorized(
            SupportState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome);
    }

    public async ValueTask<ClassifyResult> ExecuteAsync(
        PipelineMessage<SupportState> pipeline,
        CancellationToken cancellationToken)
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return new ClassifyResult.Categorized(
            result.State,
            result.Runtime,
            result.LatestOutcome!);
    }
}
```

Tandem owns model-loop execution, session persistence, usage, lifecycle receipt
replay, and runtime bookkeeping. The pipeline owns prompts, capabilities,
structured-output validation, state changes, and the meaning of each result.

## Durable Request Handoffs

Use `PipelineNodes.Request` when execution must leave the process and later resume
with a typed response:

```csharp
var customerReply = PipelineNodes.Request<
    SupportState,
    CustomerQuestion,
    CustomerReply
>(
    requestStepId: "support-ask-customer",
    portId: "CustomerReply",
    resumeStepId: "support-apply-reply",
    createRequest: SupportPolicies.BuildCustomerQuestion,
    applyResponse: SupportPolicies.ApplyCustomerReply);
```

The returned handoff exposes `Request`, `Port`, and `Resume` nodes for composition.
Tandem preserves and restores the complete `PipelineMessage<SupportState>` around
the port. Userland owns only the pure request projection and response state
transition; it does not use execution contexts, checkpoint scopes, or JSON
storage. The compiled Support sample demonstrates in-process and durable
suspension/resumption.

## Minimal Real Pipeline

```csharp
using Dunet;
using Microsoft.Extensions.DependencyInjection;
using Tandem;
using Tandem.Domain;

public sealed record ReleaseState(string Version, bool Published);

[PipelineStage("publish")]
public sealed partial class PublishStage
{
    [Union]
    public partial record PublishResult
    {
        public partial record Published(ReleaseState State);
        public partial record Rejected(ReleaseState State, string Reason);
    }

    public ValueTask<PublishResult> ExecuteAsync(
        PipelineMessage<ReleaseState> pipeline,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<PublishResult>(
        new PublishResult.Published(pipeline.State with { Published = true })
    );
}

[PipelineStage("complete")]
public sealed partial class CompleteStage
{
    [Union]
    public partial record CompleteResult
    {
        public partial record Completed(ReleaseState State);
    }

    public ValueTask<CompleteResult> ExecuteAsync(
        PipelineMessage<ReleaseState> pipeline,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<CompleteResult>(
        new CompleteResult.Completed(pipeline.State)
    );
}

public sealed record ReleaseSteps(PublishStage Publish, CompleteStage Complete);

public sealed class ReleaseComposition(ReleaseSteps release)
{
    public Pipeline Build() =>
        TandemWorkflow
            .Start(at: release.Publish, name: "release", description: "Publish a release.")
            .Route(
                on: release.Publish.Result.Published,
                to: release.Complete,
                label: "published"
            )
            .Build(release.Complete);
}

public static class ReleaseRegistration
{
    public static IServiceCollection AddRelease(this IServiceCollection services) =>
        services
            .AddTransient<PublishStage>()
            .AddTransient<CompleteStage>()
            .AddTransient<ReleaseSteps>()
            .AddTransient<ReleaseComposition>();
}
```

Register Tandem's machinery and the pipeline with
`services.AddTandem().AddRelease()`. Inspect the exact workflow that will execute:

```csharp
var inspection = composition.Build().Inspect();
Console.WriteLine(inspection.Mermaid);
Console.WriteLine(inspection.Dot);
```

Inspection exposes composition metadata, start and step IDs, request-port IDs and
input/output type names, reflected routes and condition presence, declared output
steps, Mermaid, and Graphviz DOT. Topology comes from MAF reflection and diagrams
come directly from MAF's visualizer. Tandem retains no route registry or graph
model and does not parse either render format.

## Operator Direction

A future operator surface is expected to provide commands equivalent to:

```text
tandem graph delivery
tandem graph delivery --format mermaid
tandem graph delivery --format dot
tandem graph delivery --output delivery.mmd
tandem describe delivery
```

These commands are not implemented. Current `Tandem.Tool` commands are `run`,
`attach`, and `publish`; inspection is a library API independent of CLI selection.
