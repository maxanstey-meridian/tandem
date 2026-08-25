# Tandem Agent Instructions

Before working on Tandem, read `README.md` and `CONTRIBUTING.md`.

Preserve the Core Tandem invariants in `CONTRIBUTING.md` when designing any new
abstraction. In particular:

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

Tandem is a typed agentic pipeline SDK. The configured pipeline is the lifecycle;
agents, ordinary C# stages, and human interactions are modeled graph participants,
not services called by an application-level coordinator. MAF owns live workflow
execution, agent loops, sessions, and tool dispatch. Active runs are process-owned;
do not reintroduce generalized durability.

Core versus Advanced is a semantic boundary, not a complexity tier. Core expresses
application meaning. Advanced deliberately participates in execution mechanics.
Do not put node IDs, run IDs, invocation IDs, outcomes, serialized payloads, resume
positions, or other runtime bookkeeping in `TState`.

Keep the implementation small and use the maintained framework or SDK for
commodity behavior.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **tandem** (4663 symbols, 12094 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/tandem/context` | Codebase overview, check index freshness |
| `gitnexus://repo/tandem/clusters` | All functional areas |
| `gitnexus://repo/tandem/processes` | All execution flows |
| `gitnexus://repo/tandem/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
