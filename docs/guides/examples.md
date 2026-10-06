# Examples

## Getting started

A progression from one participant through routing, deterministic stages and persistence. It
runs locally with no model, no API key and no network access.

- [C#](https://github.com/maxanstey-meridian/tandem/tree/main/examples/getting-started)
- [TypeScript](https://github.com/maxanstey-meridian/tandem-ts/tree/main/examples/getting-started)

## Model-backed examples

| Example | What it shows |
|---|---|
| **Songwriter** | A small agent pipeline with branching and revision. It writes lyrics, lints them and revises until a proofreader accepts them. |
| **Debate** | Several agents, capabilities and session continuation. A proposer argues, a critic challenges and a judge returns a verdict. |
| **Code Writer** | Implementation, deterministic verification, typed review, loops and persistence. It implements `slugify`, verifies it against test cases and loops until a reviewer accepts it. |

The C# versions live in the
[tandem repository](https://github.com/maxanstey-meridian/tandem/tree/main/examples), and the
TypeScript versions in [tandem-ts](https://github.com/maxanstey-meridian/tandem-ts/tree/main/examples).

## Running them

Each example uses two model roles. DeepSeek (`deepseek/deepseek-v4-flash-0731`) through
[OpenRouter](https://openrouter.ai) creates the work, and a second model reviews or judges it.
By default the second role also runs through OpenRouter (`openai/gpt-5.6-sol`), so an
[OpenRouter API key](https://openrouter.ai/keys) with credit is all you need. Model calls are
billed to that account.

| Variable | Required | Purpose |
|---|---|---|
| `OPENROUTER_API_KEY` | Yes | Authenticates every OpenRouter request. |
| `TANDEM_EXAMPLE_LOCAL_BASE_URL` | No | Runs the second role against a keyless, local OpenAI-compatible Responses endpoint instead (`localhost`, `127.x.x.x` or `[::1]`). |
| `TANDEM_EXAMPLE_LOCAL_MODEL` | No | The second role's model. Defaults to `openai/gpt-5.6-sol` on OpenRouter, or `gpt-5.6-sol` locally. |

::: code-group

```sh [C#]
# From the tandem repository root
OPENROUTER_API_KEY=... dotnet run --project examples/code-writer/csharp

OPENROUTER_API_KEY=... dotnet run --project examples/debate/csharp -- \
  "Should cities remove downtown parking?"

OPENROUTER_API_KEY=... dotnet run --project examples/songwriter/csharp -- \
  "A hopeful song about coming home"
```

```sh [TypeScript]
# From a tandem-ts checkout, after pnpm install && pnpm build
cd examples/code-writer/typescript
OPENROUTER_API_KEY=... pnpm start

cd ../debate/typescript
OPENROUTER_API_KEY=... pnpm start "Should cities remove downtown parking?"

cd ../songwriter/typescript
OPENROUTER_API_KEY=... pnpm start "A hopeful song about coming home"
```

:::

Without the key, an example says so and exits with code 2. If
`TANDEM_EXAMPLE_LOCAL_BASE_URL` isn't a local http(s) URL, or can't be reached, the example
names the variable and exits instead of hanging.

Code Writer also needs Node.js for its JavaScript verifier, which runs the generated code in a
child Node process. It writes its ledger to `code-writer.sqlite3`. In TypeScript, override the
path with `TANDEM_LEDGER_PATH`. When the C# run finishes, press `q` to close the terminal view and
print the run ID and the absolute ledger path.

### Using your ChatGPT account for the second role

To run the second role on your own ChatGPT or OpenAI account, start the
[`openai-oauth`](https://github.com/EvanZhouDev/openai-oauth) proxy. `login` authenticates once,
then the second command serves `http://127.0.0.1:10531/v1`:

```sh
npx --yes openai-oauth@latest login
npx --yes openai-oauth@latest
```

Then add `TANDEM_EXAMPLE_LOCAL_BASE_URL=http://127.0.0.1:10531/v1` to the commands above.
