# Application-Owned Agent Skills Plan

## Status

Implemented against `main` after `7a8431dfa7f6729d7ce161334caee566f4edff55`.

This plan adds explicit, application-owned Agent Skills to Tandem's C# and
TypeScript authoring surfaces. It does not add ambient skill discovery or a
Tandem-specific skill protocol.

## Outcome

An application can attach an existing OpenCode-compatible Agent Skills directory
to a specific Tandem agent:

```csharp
var meridian = AgentSkill.FromDirectory(
    "/Users/max/.claude/skills/meridian"
);

var reviewer = Agent
    .Create<ReviewState>("reviewer", "Review the implementation.", chatClient)
    .WithSkills(meridian)
    .WithMessage(state => state.Request)
    .Build();
```

```ts
const meridian = skill({
  directory: "/Users/max/.claude/skills/meridian",
});

const reviewer = agent({
  id: "reviewer",
  instructions: "Review the implementation.",
  client,
  message: (state) => state.request,
  skills: [meridian],
});
```

The agent receives MAF's standard progressive-disclosure skill surface. MAF
advertises the attached skill's name and description, services `load_skill`, and
services `read_skill_resource` for files such as `references/*.md`. An authored
instruction such as "Use the meridian skill" can therefore cause the model to
load that skill without its body being copied into Tandem source.

## Boundary

The ownership model is:

- The application selects exact skill directories and assigns them to agents.
- Tandem carries that explicit declaration through its typed authoring and
  runtime configuration.
- Microsoft Agent Framework parses `SKILL.md`, advertises skills, loads skill
  bodies, and reads skill resources.
- The agent chooses when to load an available skill.

Skills are authored instruction modules. They are not capabilities and do not
grant application authority. They do not enter `TState`, become graph nodes,
produce accepted values, or influence route selection except through the
agent's ordinary accepted output or capability use.

This preserves Tandem's invariants:

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

The selected skill set is application composition. MAF's discovery, loading,
resource reads, and provider lifecycle remain runtime mechanics below Tandem's
authoring seam.

## Scope

Version one supports:

- exact application-selected skill directories;
- existing `SKILL.md` frontmatter and instruction bodies;
- MAF-supported read-only resource files;
- MAF's `load_skill` and `read_skill_resource` protocol;
- reusable skill declarations attached to one or more agents;
- ordinary Core C# agents;
- Advanced harness agents;
- TypeScript agents through the existing Node API bridge.

Version one deliberately excludes:

- current-working-directory discovery;
- automatic scanning of `.opencode`, `.claude`, or home directories;
- selecting a parent directory that implicitly authorizes every child skill;
- executable skill scripts;
- executable use of `run_skill_script`;
- remote, database, or MCP skill sources;
- dynamic resources backed by application callbacks;
- inline/copy-pasted skill definitions;
- loading `AGENTS.md` or `CLAUDE.md` as implicit instructions;
- persisting loaded skill content in the Tandem ledger.

Inline definitions and additional source types can be added later when a real
consumer requires them. They are not needed to make existing OpenCode skill
packages usable.

## Existing Tandem Concepts

### Core authoring

`src/Tandem/Authoring/AgentSdk.cs` owns `AgentBuilder<TState>`. It already
collects authored instructions, capabilities, message construction, output
contracts, continuation policy, persistence, timeout, and internal
implementation configuration. Skill assignment belongs on this builder beside
the other per-agent declarations.

`AgentBuilder<TState>.Build()` creates the internal `AgentBlockConfig<TState>`
from `src/Tandem/Domain/AgentBlockConfig.cs`. The selected skills must be carried
through this immutable configuration rather than read from process-global state
inside the runtime.

### Core runtime

`src/Tandem/Infrastructure/Blocks/AgentBlock.cs` constructs the live MAF agent in
`CreateAgent`. It currently creates `ChatClientAgentOptions`, installs Tandem's
chat options and tools, and delegates agent creation through the configured
`AgentImplementation`.

MAF 1.16 exposes `ChatClientAgentOptions.AIContextProviders`.
`AgentSkillsProvider` is an `AIContextProvider`, so ordinary Core agents do not
need to become harness agents to use skills.

`src/Tandem/Infrastructure/AgentImplementation.cs` defines the internal
implementation seam used by Core and Advanced. Skill descriptors must be
available through that seam so both implementations receive the same explicit
application selection.

### Advanced harness

`src/Tandem.Advanced/HarnessAgentImplementation.cs` currently sets:

```csharp
DisableAgentSkillsProvider = true
```

This is correct when no explicit skills are attached. MAF's harness defaults to
discovering file-based skills from the process current working directory when
the provider is enabled without an `AgentSkillsSource`. Tandem must never enter
that state.

When explicit skills are attached, the harness implementation should set an
explicit source and enable the provider. Core and Advanced must use the same
internal file-source construction policy.

### TypeScript authoring

`typescript/packages/sdk/src/index.ts` defines `AgentDefinition<TState,
TOutput>`, `AgentImplementation`, `agent(...)`, and graph compilation. Agent
skills should be another immutable, reusable authored value stored by
`AgentImplementation` and serialized by `compileNode`.

The TypeScript package must not read `SKILL.md`, parse YAML, discover resources,
or reproduce MAF's skill protocol. It sends the explicitly selected directory
to the .NET runtime.

### TypeScript bridge

`typescript/bridge/RegistrationContract.cs` defines the lockstep registration
contract. `RegisteredNodeContract` carries agent instructions, client, output,
capabilities, continuation, and timeout.

`typescript/bridge/RegistrationContractValidator.cs` validates this external
JSON boundary before graph construction.

`typescript/bridge/RegisteredParticipants.cs` translates registered agent nodes
to the ordinary Core `AgentBuilder<JavaScriptState>`. Skill directories should
be translated through the same public Core API used by C# applications. The
bridge must not create a separate skills provider.

The current registration contract version is 5. Adding agent skill directories
is a contract change and increments it to 6. Tandem's TypeScript SDK and runtime
remain lockstep; no compatibility shim is required.

## MAF Delegation

MAF 1.16 already provides the commodity behavior:

- `AgentFileSkillsSource` discovers `SKILL.md` files;
- `AgentSkillsProvider` advertises skill metadata;
- `load_skill` returns the skill body;
- `read_skill_resource` returns supplementary files;
- `AgentFileSkillsSourceOptions` controls resource and script discovery.

Tandem should not implement YAML parsing, resource path resolution, progressive
disclosure prompts, or skill tools.

OpenCode skills inspected during planning use the expected Agent Skills shape:

```text
meridian/
  SKILL.md
  references/
    backend-pa-vsa.md
    coding-philosophy.md
    testing-philosophy.md
```

Their `SKILL.md` files have `name` and `description` YAML frontmatter compatible
with MAF's `AgentSkillFrontmatter` validation.

## Public C# API

Add `src/Tandem/Authoring/AgentSkill.cs`:

```csharp
namespace Tandem;

public sealed class AgentSkill
{
    public string DirectoryPath { get; }

    private AgentSkill(string directoryPath);

    public static AgentSkill FromDirectory(string directoryPath);
}
```

`FromDirectory` must:

- reject null, empty, and whitespace-only paths;
- normalize with `Path.GetFullPath`;
- require the exact directory to exist;
- require `<directory>/SKILL.md` to exist;
- retain the normalized exact directory path.

It should not parse frontmatter. MAF remains authoritative for Agent Skills
format validation.

Add fluent methods to `AgentBuilder<TState>`:

```csharp
public AgentBuilder<TState> WithSkill(AgentSkill skill);

public AgentBuilder<TState> WithSkills(params AgentSkill[] skills);
```

Composition rules:

- reject null skill values;
- reject an empty `WithSkills` call only if that matches nearby builder style;
- reject duplicate normalized directories on one agent;
- preserve attachment order for deterministic MAF advertisement;
- allow one `AgentSkill` value to be attached to multiple agents.

Do not expose MAF types from the public Tandem API.

## Internal C# Model

Carry a small internal descriptor through `AgentBuilder<TState>` and
`AgentBlockConfig<TState>`:

```csharp
internal sealed record AgentSkillDescriptor(string DirectoryPath);
```

The public `AgentSkill` owns or exposes this descriptor internally, following
the existing `AgentCapability<TState>` and descriptor pattern where practical.

Extend `AgentImplementationContext` so custom implementations receive the
selected descriptors. Do not make custom implementations rediscover application
configuration.

Add one internal MAF adapter/factory in `src/Tandem/Infrastructure`, for example:

```csharp
internal static class AgentSkillRuntime
{
    public static AgentSkillsSource CreateSource(
        IReadOnlyList<AgentSkillDescriptor> skills
    );

    public static AgentSkillsProvider CreateProvider(
        IReadOnlyList<AgentSkillDescriptor> skills
    );
}
```

The final shape may be smaller if one method is sufficient. The important
constraint is one authoritative Core adapter used by ordinary and harness
implementations.

Configure `AgentFileSkillsSourceOptions` with scripts disabled:

```csharp
new AgentFileSkillsSourceOptions
{
    ScriptFilter = _ => false,
}
```

Retain MAF's supported resource extensions unless tests reveal a concrete
OpenCode compatibility gap. Do not broaden arbitrary file access preemptively.

When no skills are attached, do not create an empty source/provider. Preserve
the current Core prompt and tool surface exactly.

## Core Runtime Wiring

For the default implementation in `AgentBlock.CreateAgent`:

1. Build the existing `ChatOptions` and explicit capability tools unchanged.
2. If skills exist, create a fresh explicit MAF source/provider.
3. Add the provider to `ChatClientAgentOptions.AIContextProviders`.
4. Construct the MAF agent through the existing implementation seam.

Provider/source lifetime must be characterized during implementation. MAF skill
sources and providers are disposable, while `AgentBlock` constructs live agents
per execution visit. Prefer a fresh provider per live agent so invocation state
and ownership cannot leak across agents or runs. Verify whether `ChatClientAgent`
disposes its context providers; if not, arrange explicit disposal without
retaining process-global providers. Content-only file sources should not be
assumed harmless merely because they hold no script runner.

The read-only MAF tools are authorized by explicit skill attachment. They do not
mutate application state and are not Tandem lifecycle capabilities. Existing
Tandem capabilities remain the only route to accepted `TState` changes.

## Advanced Runtime Wiring

Update `HarnessAgentImplementation.Create`:

- no skills: retain `DisableAgentSkillsProvider = true`;
- skills attached: set `DisableAgentSkillsProvider = false` and provide the
  explicit `AgentSkillsSource`;
- always disable scripts through the shared Core source policy;
- never enable the provider with `AgentSkillsSource = null`.

Workspace configuration remains separate. A skill directory is selected during
application composition and is not inferred from `WithWorkspace`. A `SKILL.md`
inside the workspace is ordinary workspace evidence unless separately attached
as an `AgentSkill`.

## Public TypeScript API

Add a reusable value:

```ts
export interface AgentSkill {
  readonly directory: string;
}

export function skill(definition: {
  readonly directory: string;
}): AgentSkill;
```

Add to `AgentDefinition<TState, TOutput>`:

```ts
readonly skills?: readonly AgentSkill[];
```

The factory must reject an empty or whitespace-only directory. Agent
construction must reject duplicate directory strings. The .NET boundary remains
responsible for filesystem existence, normalization, and `SKILL.md` checks
because that is the runtime host where the path is meaningful.

Do not use Node filesystem APIs in the SDK authoring package. This keeps graph
declaration deterministic and allows declaration and execution environments to
remain distinct where the runtime contract supports that.

The compiled registration object includes `skillDirectories`, preserving
declaration order.

## TypeScript Bridge Contract

Extend `RegisteredNodeContract` with:

```csharp
string[]? SkillDirectories
```

Contract rules:

- agent nodes require a non-null array, which may be empty;
- non-agent nodes forbid the field;
- entries must be non-null and nonblank;
- duplicate entries are rejected;
- path existence and `SKILL.md` validation occur through
  `AgentSkill.FromDirectory` during participant construction;
- callback-reference validation is unchanged.

Increment the SDK registration payload and bridge validator from contract
version 5 to 6.

In `RegisteredParticipantFactory.CreateAgentAsync`, attach skills through Core:

```csharp
foreach (var directory in node.SkillDirectories ?? [])
{
    builder.WithSkill(AgentSkill.FromDirectory(directory));
}
```

This is intentionally the same path used by native C# consumers.

## Security And Authority

Skill sources are prompt-injection trust boundaries. This design requires the
application to select each exact directory. Tandem must not accept a broad root
and silently authorize every discovered child skill.

For version one:

- `ScriptFilter` always returns false;
- no script runner is supplied;
- MAF currently advertises `run_skill_script` even when no scripts are
  discoverable; script approval remains enabled and every file script is
  filtered from the source;
- symlink and traversal behavior remains MAF's maintained responsibility;
- Tandem validates only the explicitly selected directory and `SKILL.md` entry
  point;
- resources remain read-only context and cannot update `TState`;
- loaded skill instructions remain subordinate to Tandem's runtime instructions
  and the agent's application-authored instructions;
- skill attachment grants no Tandem capability or workspace mutation authority.

If script support is proposed later, it requires a separate design covering
capability authorization, tool-effect classification, interception, human
approval, observation, cancellation, and execution isolation. It is not an
incremental boolean option on this feature.

## Validation And Failure Semantics

Declared configuration failures should occur before model execution:

- invalid/blank path: `AgentSkill.FromDirectory` argument failure;
- directory missing: composition/build failure with the normalized path;
- `SKILL.md` missing: composition/build failure with the expected path;
- malformed skill frontmatter: MAF source/provider initialization failure before
  the first model request;
- duplicate attachment: builder/SDK authoring failure;
- invalid bridge shape: registration validation failure.

Do not catch and convert arbitrary filesystem or MAF failures into successful
pipeline outcomes. These are undeclared runtime/configuration failures and
remain exceptions.

## Tests

### Core integration tests

Create temporary real skill directories rather than mocking MAF:

```text
selected-skill/
  SKILL.md
  references/rules.md
  scripts/unsafe.sh

neighbour-skill/
  SKILL.md
```

Use the existing scripted/recording `IChatClient` patterns in
`tests/Tandem.Tests/Infrastructure/LocalCapabilityTests.cs` and related agent
tests.

Cover:

1. An agent without skills has no skill advertisement or skill tools.
2. An attached skill advertises its frontmatter name and description.
3. A model can invoke `load_skill` and receive the exact `SKILL.md` body.
4. A model can invoke `read_skill_resource` and receive
   `references/rules.md`.
5. `run_skill_script` is advertised by MAF but no script is discoverable or
   executable.
6. `scripts/unsafe.sh` is not exposed as a resource.
7. An unattached neighbouring skill is not discovered.
8. Different agents receive only their attached skills.
9. Reusing one `AgentSkill` across agents works.
10. Missing directories fail before a model request.
11. Missing `SKILL.md` fails before a model request.
12. Duplicate directories fail during composition.
13. Skills compose with existing capabilities and structured output.
14. Continued sessions do not leak skill availability between agents or runs.

At least one test should use an OpenCode-shaped fixture with a `references/`
directory, not merely a one-file synthetic skill.

### Advanced tests

Cover:

1. `UseHarness` plus an explicit skill supports `load_skill` and resource reads.
2. `UseHarness` without skills keeps the skills provider disabled.
3. A `SKILL.md` in the process current directory is not discovered.
4. A `SKILL.md` in an attached workspace is not discovered unless separately
   attached as an `AgentSkill`.
5. Harness file tools and skill resource tools retain their separate authority
   boundaries.

Add a characterization assertion for MAF's relevant tool names and source
options if Tandem depends on constants not already protected by
`MafBindingCharacterizationTests`.

### Bridge validation tests

Update `typescript/bridge-tests/RegistrationContractValidatorTests.cs` for
contract version 6 and cover:

- valid empty and populated agent skill arrays;
- null array rejection for agent nodes;
- field rejection on non-agent nodes;
- null/blank entry rejection;
- duplicate entry rejection;
- no new callback references.

Add participant construction coverage proving bridge paths are translated
through `AgentSkill.FromDirectory` and the Core builder.

### TypeScript authoring and type tests

Update the positive fixture to demonstrate reusable skills attached to an
agent. Add negative cases for malformed definitions and invalid agent skill
values.

Runtime tests must use a temporary real skill directory and the existing local
OpenAI-compatible fixture. The fixture should:

1. inspect the advertised skill tools;
2. return a `load_skill` tool call;
3. inspect the subsequent request for loaded skill content;
4. return a `read_skill_resource` call;
5. verify resource content reaches the model;
6. complete the Tandem agent normally.

Exercise the same behavior through packed tarballs in the packed-consumer gate,
not only against workspace source.

## Documentation

Update the root `README.md` with the C# example and
`typescript/README.md` with the TypeScript example.

Document:

- Tandem accepts exact Agent Skills directories containing `SKILL.md`;
- existing OpenCode/Claude-compatible skills can be reused directly;
- MAF owns progressive disclosure and resource reads;
- scripts are deliberately disabled;
- skills do not grant Tandem capabilities or workspace mutation rights;
- Tandem never scans `cwd`, agent workspaces, OpenCode configuration, or home
  directories for skills.

Update TypeScript contract-version references and the stabilization plan to
record version 6 and the new runtime fixture.

Suggested concise contract wording:

> Skills are application-selected instruction packages. Attaching a skill makes
> its `SKILL.md` and read-only resources discoverable to that agent through
> MAF's standard skill protocol. Skills do not grant pipeline capabilities or
> execution authority.

## Implementation Sequence

1. Add `AgentSkill.FromDirectory` and its validation tests.
2. Add `WithSkill`/`WithSkills` to `AgentBuilder<TState>` and carry descriptors
   through `AgentBlockConfig<TState>`.
3. Characterize MAF provider/source lifetime and exact tool behavior with a
   focused integration test.
4. Add the shared internal MAF file-source/provider adapter with scripts
   disabled.
5. Wire ordinary Core agents through `ChatClientAgentOptions.AIContextProviders`.
6. Wire Advanced harness agents through the same explicit source policy.
7. Add Core and Advanced real-filesystem integration tests.
8. Add TypeScript `skill({ directory })` and `skills` authoring support.
9. Increment the bridge registration contract to version 6.
10. Add bridge validation and translate paths through the Core API.
11. Add TypeScript type, runtime, and packed-consumer tests.
12. Update C# and TypeScript documentation.
13. Run formatting, all .NET tests, all TypeScript checks, package verification,
    and `~/Sites/plumb/plumb . --json`.

## Acceptance Criteria

The feature is complete when:

- a native C# agent can use an existing OpenCode skill directory;
- a TypeScript agent can use the same directory without TS parsing or copying
  its content;
- `load_skill` and `read_skill_resource` are serviced by MAF;
- referenced Meridian-style Markdown resources are readable on demand;
- skill scripts cannot be discovered or executed; MAF's always-advertised
  `run_skill_script` remains approval-gated;
- an unattached skill cannot be discovered from `cwd`, workspace, or a sibling
  directory;
- attaching a skill does not grant state mutation or lifecycle authority;
- Core and Advanced use one internal source policy;
- SDK/runtime contract version 6 rejects malformed registrations;
- all C#, bridge, Node, type, package, and mechanical architecture gates pass.

## Known Implementation Risk

The principal risk is MAF provider/source lifetime. Tandem creates live agents
per execution visit, while `AgentSkillsProvider` and `AgentSkillsSource` are
disposable and may own cached discovery state. The implementation must verify
ownership under `ChatClientAgent` and `HarnessAgent`, then use a fresh,
run-bounded provider/source or explicitly dispose it. Do not introduce a global
skill provider merely to avoid this question; that would risk cross-agent state
and stale filesystem content.
