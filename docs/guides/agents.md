# Agents

An agent is a model-backed participant. It has:

- a stable identity
- instructions
- a model client
- a message built from current state
- optional model request controls
- optional typed capabilities
- optional structured output
- optional session continuation

::: code-group

```csharp [C#]
var reviewer = Agent
    .Create<CodeWriterState>(
        // Routes and ledger entries refer to this stable name.
        "reviewer",
        // Keep the role narrow: judge the exact candidate and evidence.
        "Review the exact implementation against the requirements and passing verification evidence.",
        // The host chooses which model performs this role.
        clients.Reviewer)
    // Build each visit from the latest accepted candidate and checks.
    .WithMessage(state =>
        $"Requirements: {JsonSerializer.Serialize(state.Requirements)}\n"
        + $"Exact source: {state.Implementation!.Source}\n"
        + $"Passing verification evidence: {JsonSerializer.Serialize(state.Verification)}")
    .WithOutput(
        // This definition owns the response shape and validation.
        new ReviewDecisionOutput(),
        // Only an accepted decision is allowed to update state.
        (state, review) => state.RecordReview(review))
    .Build();
```

```ts [TypeScript]
const reviewer = agent<State, ReviewDecision>({
  // Routes and ledger entries refer to this stable name.
  id: "reviewer",
  // Keep the role narrow: judge the exact candidate and evidence.
  instructions:
    "Review the exact implementation against the requirements and passing verification evidence.",
  // The host chooses which model performs this role.
  client: clients.reviewer,

  // Build each visit from the latest facts, not hidden conversation state.
  message: (state) =>
    [
      `Requirements: ${JSON.stringify(state.requirements)}`,
      `Exact source: ${state.implementation!.source}`,
      `Passing verification evidence: ${JSON.stringify(state.verification)}`,
    ].join("\n"),

  output: {
    // Ask for the decision the application needs, not arbitrary prose.
    instructions: "Return Accept or RequestChanges with a concise summary and concrete findings.",
    // Tandem corrects anything that doesn't match this shape.
    schema: ReviewDecision,
    // Only an accepted decision is allowed to update state.
    apply: recordReview,
  },
});
```

:::

Clients are covered in [Chat Clients](/reference/chat-clients).

## Structured output becomes state

Code Writer doesn't ask the reviewer for prose and then ask another model what the prose
meant. It asks for a typed decision:

::: code-group

```csharp [C#]
public enum ReviewDisposition
{
    // The candidate may leave the graph successfully.
    Accept,
    // The candidate needs another implementer turn.
    RequestChanges,
}

public sealed record ReviewDecision(
    // Routes use this value to finish or loop.
    ReviewDisposition Decision,
    // The caller can show this account directly.
    string Summary,
    // Requested changes carry concrete work for the next turn.
    IReadOnlyList<string> Findings
);
```

```ts [TypeScript]
export const ReviewDecision = z
  .object({
    // The graph only needs one of these two decisions.
    decision: z.enum(["Accept", "RequestChanges"]),
    // Give the caller a concise account of the review.
    summary: z.string().min(1),
    // Requested changes must say exactly what needs fixing.
    findings: z.array(z.string().min(1)),
  })
  // Don't allow an empty RequestChanges response into state.
  .refine((review) => review.decision !== "RequestChanges" || review.findings.length > 0, {
    path: ["findings"],
    message: "RequestChanges requires at least one finding",
  });
```

:::

The output is validated before Tandem applies it to state. An invalid response gets exactly
one corrective turn in the same session. If the correction is still invalid, the agent fails
with the raw response and the validation problems.

After that, `state.Review?.Decision == ReviewDisposition.Accept` is just an ordinary
application fact, and a [route](/guides/routes) can make an ordinary decision from it.

### Raw output

Some specialised models need a plain user message and return plain text. In TypeScript, set
`raw: true` with a `parse` function. If you also set both `instructions` and
`output.instructions` to `""`, Tandem sends the authored message with no system instructions
and no response-format constraint. In C#, raw parsing is an Advanced API:
implement `IAgentRawOutputDefinition<TState, TOutput>` and call `WithRawOutput(definition,
apply)`.

## Model request controls

Different roles need different model behaviour. A reviewer might need repeatable decisions and
a bounded answer, while another agent uses the provider's defaults. Set those controls where
the role is defined:

::: code-group

```csharp [C#]
var reviewer = Agent
    .Create<State>("reviewer", "Review the exact implementation against the requirements.", clients.Reviewer)
    .WithModelRequestOptions(
        new AgentModelRequestOptions(
            // Explicitly ask a compatible model not to spend tokens reasoning.
            reasoningEffort: AgentReasoningEffort.None,
            // Prefer repeatable decisions for this role.
            temperature: 0,
            // Bound the complete response, including structured output.
            maxOutputTokens: 4096))
    .WithMessage(ReviewerMessage)
    .WithOutput(new ReviewDecisionOutput(), (state, review) => state.RecordReview(review))
    .Build();
```

```ts [TypeScript]
const reviewer = agent<State, ReviewDecision>({
  id: "reviewer",
  instructions: "Review the exact implementation against the requirements.",
  client: clients.reviewer,
  message: reviewerMessage,

  // Explicitly ask a compatible model not to spend tokens reasoning.
  reasoning: { effort: "none" },
  // Prefer repeatable decisions for this role.
  temperature: 0,
  // Bound the complete response, including structured output.
  maxOutputTokens: 4096,

  output: {
    instructions: "Return Accept or RequestChanges with concrete findings.",
    schema: ReviewDecision,
    apply: recordReview,
  },
});
```

:::

- Reasoning is either an effort (`none`, `low`, `medium` or `high`) or a reasoning token
  budget of at least 1024 (`reasoning: { maxTokens }` in TypeScript, `reasoningMaxTokens` in
  C#), never both. Omitting it expresses no preference.
- Temperature must be between `0` and `2`.
- Maximum output tokens must be a positive 32-bit integer.

These settings stay attached to the agent during capability calls and structured-output
correction turns. The endpoint still decides which settings it supports. They're model
request settings, not application facts, so they don't belong in state and don't affect
routing.

Agents can also be bounded in time, with `WithTimeout(TimeSpan)` in C# or `timeoutMs` in
TypeScript.

## Sessions

An agent starts a fresh session on each visit by default. If the graph can route work back to
the same agent, for example after failed verification, keep its conversation with
`.ContinueSession()` in C# or `continueSession: true` in TypeScript.

## Agent Skills

Attach an existing Agent Skills or OpenCode skill directory to a specific agent:

::: code-group

```csharp [C#]
var meridian = AgentSkill.FromDirectory("/path/to/skills/meridian");

var reviewer = Agent
    .Create<ReviewState>("reviewer", "Use the meridian skill to review the design.", client)
    .WithSkill(meridian)
    .WithMessage(state => state.Request)
    .Build();
```

```ts [TypeScript]
const reviewer = agent<ReviewState>({
  id: "reviewer",
  instructions: "Use the meridian skill to review the design.",
  client,
  message: (state) => state.request,
  skills: [skill({ directory: "/path/to/skills/meridian" })],
});
```

:::

The directory must contain `SKILL.md`. Microsoft Agent Framework owns progressive disclosure,
`load_skill`, and read-only access to resources such as `references/*.md`. Tandem never scans
the working directory, an agent workspace, OpenCode configuration or home directories for
skills.

Skills are instruction packages, not capabilities. Attaching one grants no state transition,
lifecycle action or workspace mutation authority. Script files are filtered out of the skill
and can't execute. MAF currently still advertises its approval-gated `run_skill_script` tool,
even when no scripts are available.

## Next

- Give an agent something to do with [Capabilities](/guides/capabilities)
- Give an agent a repository to work in with [Workspace Tools](/guides/workspace-tools)
