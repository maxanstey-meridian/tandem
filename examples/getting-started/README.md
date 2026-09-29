# Getting Started

These examples reference the repository's Tandem projects, like the other examples here, and build
as part of `Tandem.slnx`.

1. `01-single-pipeline` runs one typed participant.
2. `02-routing` makes a domain decision in state and routes to one of two outputs.
3. `03-stage` inserts a normal deterministic operation into the lifecycle.
4. `04-persistence` records accepted values in SQLite.

Each is an independent project under [`csharp`](csharp). Run one from the repository root with, for
example, `dotnet run --project examples/getting-started/csharp/01-single-pipeline`.

The TypeScript equivalents live in the
[`tandem-ts` repository](https://github.com/maxanstey-meridian/tandem-ts) under
`examples/getting-started/typescript`.

The Songwriter, Debate, and Code Writer examples remain the larger showcases.
