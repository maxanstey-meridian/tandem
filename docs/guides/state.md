# State

Every participant in a pipeline operates over the same application-owned state type. It's
the single source of truth for what the run knows.

For Code Writer, the important facts are:

::: code-group

```csharp [C#]
public sealed record CodeWriterState(
    // The job both agents are working towards.
    IReadOnlyList<string> Requirements,
    // Source and rationale accepted from the implementer.
    ImplementationCandidate? Implementation = null,
    // Evidence produced by normal C# verification.
    VerificationResult? Verification = null,
    // The reviewer's accepted decision.
    ReviewDecision? Review = null
);
```

```ts [TypeScript]
import { z } from "zod";

export const State = z.object({
  // The job both agents are working towards.
  requirements: z.array(z.string().min(1)).min(1),
  // Source and rationale accepted from the implementer.
  implementation: ImplementationCandidate.nullable(),
  // Evidence produced by running normal verification code.
  verification: VerificationResult.nullable(),
  // The reviewer's accepted decision.
  review: ReviewDecision.nullable(),
});

// The schema is also the single source of the TypeScript type.
export type State = z.infer<typeof State>;
```

:::

## Application facts only

State contains **application facts**. It doesn't hold Tandem bookkeeping such as the current
node, the run ID, an invocation ID, a route name or a resume position. Tandem tracks those
itself.

If another participant needs to know something, put it in state explicitly and strongly
typed. When something meaningful changes, return a new state describing the new facts, and
let the graph decide where those facts send the run next.

In C#, state is any type you like, and records with `with` expressions work well. In
TypeScript, state crosses into the .NET runtime as JSON validated against the Zod schema.
