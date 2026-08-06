import type { Todo } from "./types.js";

export class TodoStore {
  private readonly todos = new Map<string, Todo>();

  add(todo: Todo): void {
    this.todos.set(todo.id, todo);
  }

  get(id: string): Todo | undefined {
    return this.todos.get(id);
  }

  all(): Todo[] {
    return Array.from(this.todos.values());
  }

  remove(id: string): boolean {
    return this.todos.delete(id);
  }
}
