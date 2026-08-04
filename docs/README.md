# Tandem Build Plans

Read `AUTHORITATIVE-ARCHITECTURE.md` before using these plans. It defines the
product and its non-negotiable architecture. These plans define only the order
in which to build it.

The existing application is not a migration source. Implementation starts from
an empty app after the current code is removed.

## Build Order

### 1. First Running Block

`01-first-running-block.md`

Build the thinnest real Tandem journey: read a packet, create an isolated Git
workspace, run one configured MAF Harness agent as one MAF Workflow block, and
show its result in the CLI.

This proves the chosen framework, model provider, workspace, and basic execution
path together before building the pipeline around them.

### 2. Configured Pipeline

`02-configured-pipeline.md`

Turn the working block into the actual LEGO pipeline: shared context,
conditional routes, lifecycle MCP termination, durable continuation, and the
configured planner, executor, command-check, and review blocks.

This proves that changing composition changes the lifecycle without changing
the runtime.

### 3. Operator Experience

`03-operator-experience.md`

Add streamed run events, the terminal dashboard, human suspension and
resumption, restart behavior, and the complete real user journey.

This finishes the MVP as an operable application rather than a framework demo.

## Working Rule

Build the plans in order. Each plan must finish with its described journey
running end to end before work starts on the next one.
