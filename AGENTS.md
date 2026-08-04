# Tandem Agent Instructions

Before working on Tandem, read these documents in order:

1. `docs/AUTHORITATIVE-ARCHITECTURE.md`
2. `docs/README.md`
3. The current numbered implementation plan.

Build the numbered plans in order. Finish each plan's real end-to-end proof before
starting the next.

The configured pipeline is the lifecycle. Blocks perform operations; MAF
Workflow composition selects successors. MAF owns orchestration, durability,
agent loops, and tool dispatch.

Keep the implementation small. Add only machinery required by the current plan
and use the maintained framework or SDK for commodity behavior.
