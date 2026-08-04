import type { Todo } from "./types.js";
import { TodoStore } from "./store.js";

export class TodoService {
  private readonly store = new TodoStore();

  create(title: string): Todo {
    const todo: Todo = {
      id: crypto.randomUUID(),
      title,
      completed: false,
    };
    this.store.add(todo);
    return todo;
  }

  get(id: string): Todo | undefined {
    return this.store.get(id);
  }

  list(): Todo[] {
    return this.store.all();
  }

  delete(id: string): boolean {
    return this.store.remove(id);
  }
}