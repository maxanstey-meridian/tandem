import {
  Project,
  Node,
  QuoteKind,
  SyntaxKind,
  type ArrayLiteralExpression,
  type CallExpression,
  type ObjectLiteralExpression,
} from "ts-morph";

export type RouteEdit =
  | { kind: "delete"; order: number }
  | { kind: "move"; order: number; toOrder: number }
  | {
      kind: "update";
      order: number;
      from?: string;
      to?: string;
      label?: string;
      outcome?: "success" | "failed" | null;
      when?: string | null;
    }
  | {
      kind: "insert";
      order: number;
      from: string;
      to: string;
      label: string;
      outcome?: "success" | "failed";
      when?: string;
    };

export async function editDirectRoute(
  file: string,
  pipelineName: string,
  edit: RouteEdit,
): Promise<string> {
  const project = new Project({
    manipulationSettings: { quoteKind: QuoteKind.Double },
    tsConfigFilePath: undefined,
  });
  const source = project.addSourceFileAtPath(file);
  const arrays = source
    .getDescendantsOfKind(SyntaxKind.CallExpression)
    .filter((call) => call.getExpression().getText() === "pipeline")
    .map((call) => call.getArguments()[0])
    .filter(Node.isObjectLiteralExpression)
    .filter((object) => {
      const property = object.getProperty("name");
      const initializer = Node.isPropertyAssignment(property)
        ? property.getInitializer()
        : undefined;
      return Node.isStringLiteral(initializer) && initializer.getLiteralValue() === pipelineName;
    })
    .map((object) => object.getProperty("routes"))
    .filter(Node.isPropertyAssignment)
    .map((property) => property.getInitializer())
    .filter(Node.isArrayLiteralExpression);
  if (arrays.length !== 1) {
    throw new Error(
      "Routes are read-only: a single direct pipeline routes array could not be identified.",
    );
  }
  const array = arrays[0] as ArrayLiteralExpression;
  if (edit.kind === "insert") {
    const properties = [
      `from: ${edit.from}`,
      `to: ${edit.to}`,
      `label: ${JSON.stringify(edit.label)}`,
    ];
    if (edit.outcome) {
      properties.push(`outcome: ${JSON.stringify(edit.outcome)}`);
    }
    if (edit.when) {
      properties.push(`when: ${edit.when}`);
    }
    array.insertElement(edit.order, `route({ ${properties.join(", ")} })`);
  } else {
    const element = array.getElements()[edit.order];
    if (
      !Node.isCallExpression(element) ||
      element.getExpression().getText() !== "route" ||
      !Node.isObjectLiteralExpression(element.getArguments()[0])
    ) {
      throw new Error(`Direct route ${edit.order} is helper-generated or ambiguous.`);
    }
    const call = element;
    if (edit.kind === "delete") {
      array.removeElement(edit.order);
    } else if (edit.kind === "move") {
      const text = call.getText();
      array.removeElement(edit.order);
      array.insertElement(edit.toOrder, text);
    } else {
      update(call, edit);
    }
  }
  await source.formatText();
  return source.getFullText();
}
function update(call: CallExpression, edit: Extract<RouteEdit, { kind: "update" }>): void {
  const object = call.getArguments()[0] as ObjectLiteralExpression;
  const set = (name: string, value: string | null | undefined) => {
    if (value === undefined) {
      return;
    }
    const existing = object.getProperty(name);
    if (value === null) {
      existing?.remove();
    } else if (existing && Node.isPropertyAssignment(existing)) {
      existing.setInitializer(value);
    } else {
      object.addPropertyAssignment({ name, initializer: value });
    }
  };
  set("from", edit.from);
  set("to", edit.to);
  set("label", edit.label === undefined ? undefined : JSON.stringify(edit.label));
  set(
    "outcome",
    edit.outcome === undefined
      ? undefined
      : edit.outcome === null
        ? null
        : JSON.stringify(edit.outcome),
  );
  set("when", edit.when);
}
