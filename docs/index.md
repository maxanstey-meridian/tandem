---
layout: home
hero:
  name: Tandem
  text: The configured pipeline is the lifecycle
  tagline: Typed agentic pipelines for C# and TypeScript. Define the facts, the participants and the routes in code, then run and inspect it.
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: The Mental Model
      link: /guides/mental-model
features:
  - title: Facts in state
    details: Every participant reads and returns one application-owned state type. No run IDs, node IDs or resume positions hiding in it.
  - title: Decisions in routes
    details: Participants never choose their successors. Named routes read typed state and decide what runs next, in the open.
  - title: Permissions in capabilities
    details: An agent can only do what you've given it a typed, validated action for. Model output becomes application state, not prose to parse.
---

Tandem runs in-process on .NET. Microsoft Agent Framework owns the live execution underneath
it: workflow execution, model loops, sessions and tool dispatch. Tandem owns the typed
application model on top. Author a pipeline in C#, or author the same pipeline in TypeScript
through the npm package.

Start with [Getting Started](/getting-started), then read
[The Mental Model](/guides/mental-model).
