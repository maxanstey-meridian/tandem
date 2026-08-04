---
title: "Add completion filtering to the todo API"
repository: "./repo"
base: "main"
outcomes:
  - id: "mark-complete"
    description: "Add a `markComplete(id)` method to `TodoService` that sets `completed` to `true` on the matching todo. It should return the updated todo or `undefined` if not found."
  - id: "filter-by-status"
    description: "Add a `listByStatus(completed: boolean)` method to `TodoService` that returns todos filtered by their `completed` flag."
verification:
  - "grep -q 'markComplete' src/service.ts"
  - "grep -q 'listByStatus' src/service.ts"
constraints:
  - "Do not change existing method signatures."
  - "Do not add new dependencies."
  - "Do not modify src/store.ts."
---

Inspect the existing code in src/ before making changes. The Todo interface already has a `completed` boolean field — use it.