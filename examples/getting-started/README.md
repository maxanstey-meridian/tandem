# Getting Started

These examples consume Tandem through its public package names; they do not reference repository
projects or runtime files directly.

1. `01-single-pipeline` runs one typed participant.
2. `02-routing` makes a domain decision in state and routes to one of two outputs.
3. `03-stage` inserts a normal deterministic operation into the lifecycle.
4. `04-persistence` records accepted values in SQLite.

The C# programs are independent projects under [`csharp`](csharp). The TypeScript equivalents share
one package manifest under [`typescript`](typescript) and can be run from the repository root with
`pnpm --dir typescript --filter tandem-getting-started example:01` through `example:04`.

The existing Songwriter, Debate, and Code Writer examples remain the larger showcases.
