# Tandem Agent Instructions

Before working on Tandem, read `README.md` and `CONTRIBUTING.md`.

The configured pipeline is the lifecycle. Blocks perform operations; MAF
Workflow composition selects successors. MAF owns orchestration, durability,
agent loops, and tool dispatch.

Keep the implementation small and use the maintained framework or SDK for
commodity behavior.
