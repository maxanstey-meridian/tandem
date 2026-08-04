# Tandem Architecture

Status: authoritative

## Purpose

Tandem runs configurable, durable pipelines for agentic software work.

A pipeline is composed from reusable blocks. Each block receives shared context,
performs one operation, and emits observations and an outcome. Conditional routes
select the next block from that updated context and outcome.

The configured pipeline is the lifecycle. The runtime executes that lifecycle
durably.

## Runtime Model

Tandem has six core concepts:

- **Block**: one reusable operation.
- **Context**: durable state shared across the pipeline.
- **Outcome**: the result emitted by a block.
- **Condition**: a predicate over context and the latest outcome.
- **Route**: a condition paired with a destination block.
- **Prompt**: instructions contributed to an agent block.

A block implementation owns its operation. Pipeline composition owns when the
block runs and what follows it.

The execution cycle is:

1. Load the durable pipeline context and current block.
2. Run the block with that context.
3. Persist its observations and outcome.
4. Evaluate the block's configured routes in order.
5. Select the first matching destination.
6. Run the destination, suspend, or complete.

The same block implementation can appear more than once with different prompts,
model profiles, conditions, and routes.

## Platform

Tandem uses Microsoft Agent Framework for its runtime:

- MAF Workflows executes the block graph.
- MAF Durable Task persists workflow progress and resumes suspended runs.
- MAF Harness runs model and tool loops inside agent blocks.
- `IChatClient` adapters connect configured model providers and profiles.
- MCP exposes authoritative lifecycle operations to agents.

Tandem code supplies product blocks, conditions, prompts, policies, Git
operations, and operator interfaces. Framework code supplies orchestration,
durability, sessions, model loops, tool dispatch, and workflow events.

## Composition

The initial product ships one named global pipeline preset: `simple-v1`.

The preset is ordinary composition data assembled from reusable block
implementations. Changing that data can change block order, prompts, conditions,
routes, and model profiles without changing the workflow runtime or block
implementations.

Pipeline composition is application-owned. Provider and model profiles are
loaded from `$TANDEM_HOME/config.json`; the configuration file is not a general
workflow language.

A composition may express routes such as:

```text
executor asks for guidance       -> planner
planner approves                 -> executor
planner needs a human decision   -> human input
command check fails              -> executor
command check passes             -> next command check
all command checks pass          -> reviewer
reviewer requests changes        -> executor
reviewer accepts                 -> complete
```

Conditions are reusable. For example, a pipeline can route an executor outcome
containing Chinese characters to another agent block using a larger model
profile. Adding that route changes composition, not runtime code.

## Blocks

Blocks share one execution contract and may perform different kinds of work:

- Run an agent with MAF Harness.
- Run a verification command.
- Ask for human input and suspend.
- Prepare an isolated Git workspace.
- Capture a diff or commit.
- Transform or record context.

Named operations such as asking a planner, submitting an implementation report,
reviewing a change, rotating a session, or promoting a model are configured uses
of blocks and conditions.

An agent block is configured with:

- a named model profile;
- instructions and prompt contributions;
- a workspace access policy;
- available tools;
- a durable session identity;
- outgoing routes.

Planner and reviewer blocks use read-only workspace tools. Executor blocks gain
write tools when the configured pipeline has established mutation authority.

## Lifecycle Tools

Agents communicate authoritative outcomes through MCP tools. Examples include
asking for guidance, submitting an implementation report, and recording a
checkpoint.

An accepted lifecycle tool call follows one sequence:

```text
validate payload
-> persist outcome
-> terminate the active model turn mechanically
-> return control to the workflow
-> evaluate configured routes
```

The model cannot emit further effective text or tool calls after the accepted
lifecycle outcome. Function middleware around the MCP invocation provides the
mechanical termination.

## Pipeline Context

The durable context contains only facts required by blocks and routes, including:

- run identity and status;
- packet and pinned source commit;
- isolated workspace location;
- current block and accumulated outcomes;
- agent session identities;
- planner decisions and human answers;
- verification results;
- reviewed commit or diff identity;
- observable run events.

Context records facts. Routing remains in the configured workflow graph.

## Git And Verification

Each run works in its own clone pinned to the packet's resolved base commit. The
agent edits that clone rather than the source repository.

Verification commands are configured command blocks. Each command records its
exit code and output summary as a block outcome. Routes determine whether the
pipeline runs the next command, returns to an executor, or proceeds to review.

Review is grounded in the exact candidate produced by the executor and verified
by the pipeline. Branch preparation, when invoked by the operator, publishes
that accepted candidate without changing the operator's current checkout.

## Operator Interface

The CLI admits a packet, starts or resumes a run, and opens the terminal
dashboard.

The dashboard presents:

- the active block and model;
- streamed text, reasoning, and tool activity;
- verification output;
- elapsed time and context usage;
- human questions and answers;
- the final accepted result and workspace or branch location.

Workflow events are the source of live status. Human input resumes the suspended
workflow through its durable event surface.

## Product Boundary

The MVP owns:

- packet parsing;
- the `simple-v1` composition;
- product block implementations and route conditions;
- model profile resolution;
- workspace and Git operations;
- verification execution;
- mutation policy;
- run event projection;
- CLI and terminal dashboard.

MAF owns the workflow engine, durable scheduling, agent sessions, model loop,
tool loop, MCP integration, and streaming primitives.

## Acceptance Criteria

Tandem's composition is genuine when composition changes alone can demonstrate
all of the following:

1. Removing a planner route means no planner runs.
2. Inserting a block before the planner makes that block run first.
3. Multiple command checks run in configured order.
4. A failed command check routes to the configured remediation block and skips
   later checks.
5. A passed command check routes to the next configured check.
6. Removing review means no review runs.
7. Multiple review blocks run under their configured conditions.
8. Prompt contributions reach their configured agent blocks.
9. Context can trigger session rotation or model promotion through a configured
   condition.
10. An accepted lifecycle MCP call prevents any later event from that model turn
    from affecting the run.
11. A process restart resumes from durable context without blindly repeating an
    authoritative side effect.

The decisive invariant is:

```text
The configured pipeline is the lifecycle.
The runtime only executes it durably.
```
