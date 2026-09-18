import type { PipelineInspection } from "@maxanstey-meridian/tandem";
import { dirname, join } from "node:path";
import {
  Node,
  Project,
  SyntaxKind,
  type ArrayLiteralExpression,
  type CallExpression,
  type Expression,
  type ObjectLiteralExpression,
} from "ts-morph";
export interface SourceReference {
  readonly editable: boolean;
  readonly expression?: string;
  readonly file?: string;
  readonly line?: number;
  readonly reason?: string;
  readonly callbacks?: Readonly<Record<string, { readonly file: string; readonly line: number }>>;
}
export interface RouteOwnership extends SourceReference {
  readonly sourceIndex?: number;
  readonly predicate?: string;
}
export interface SourceOwnership {
  readonly routeArray?: { readonly file: string; readonly line: number; readonly length: number };
  readonly routes: readonly RouteOwnership[];
  readonly state?: {
    readonly file?: string;
    readonly line?: number;
    readonly reason?: string;
  };
  readonly participants: Readonly<Record<string, SourceReference>>;
}
export function editRevision(graph: PipelineInspection, ownership: SourceOwnership): string {
  return JSON.stringify({
    routes: graph.routes,
    routeArray: ownership.routeArray,
    routesOwnership: ownership.routes,
  });
}

export function locateOwnership(config: string, graph: PipelineInspection): SourceOwnership {
  const project = new Project({ skipAddingFilesFromTsConfig: true });
  project.addSourceFilesAtPaths([
    join(dirname(config), "**/*.ts"),
    `!${join(dirname(config), "**/node_modules/**")}`,
  ]);
  const candidates: {
    array: ArrayLiteralExpression;
    call: CallExpression;
    object: ObjectLiteralExpression;
  }[] = [];
  const pipelineCandidates: { routes: import("ts-morph").PropertyAssignment }[] = [];
  for (const source of project.getSourceFiles()) {
    for (const call of source.getDescendantsOfKind(SyntaxKind.CallExpression)) {
      if (call.getExpression().getText() !== "pipeline") {
        continue;
      }
      const object = call.getArguments()[0];
      if (!Node.isObjectLiteralExpression(object)) {
        continue;
      }
      const name = object.getProperty("name"),
        routes = object.getProperty("routes"),
        nameInitializer = Node.isPropertyAssignment(name) ? name.getInitializer() : undefined;
      if (
        !Node.isStringLiteral(nameInitializer) ||
        nameInitializer.getLiteralValue() !== graph.name
      ) {
        continue;
      }
      if (Node.isPropertyAssignment(routes)) {
        pipelineCandidates.push({ routes });
        const value = routes.getInitializer();
        if (Node.isArrayLiteralExpression(value)) {
          candidates.push({ array: value, call, object });
        }
      }
    }
  }
  if (candidates.length !== 1) {
    const location =
      pipelineCandidates.length === 1
        ? {
            file: pipelineCandidates[0]!.routes.getSourceFile().getFilePath(),
            line: pipelineCandidates[0]!.routes.getStartLineNumber(),
          }
        : undefined;
    return unreadable(
      graph,
      candidates.length
        ? "Multiple direct pipeline declarations match this pipeline name."
        : "The owning pipeline routes are helper-generated, aliased, or not a direct array.",
      location,
    );
  }
  const owner = candidates[0]!,
    participants = locateParticipants(owner.object, graph),
    routeArrayFile = owner.array.getSourceFile().getFilePath(),
    routeArrayLine = owner.array.getStartLineNumber(),
    elements = owner.array.getElements(),
    directOnly =
      elements.length === graph.routes.length &&
      elements.every(
        (element) =>
          Node.isCallExpression(element) &&
          element.getExpression().getText() === "route" &&
          Node.isObjectLiteralExpression(element.getArguments()[0]),
      );
  const routes: RouteOwnership[] = graph.routes.map(() => ({
    editable: false,
    file: routeArrayFile,
    line: routeArrayLine,
    reason:
      "No unique direct route construct owns this runtime route; open the routes array instead.",
  }));
  owner.array.getElements().forEach((element, sourceIndex) => {
    if (!Node.isCallExpression(element) || element.getExpression().getText() !== "route") {
      return;
    }
    const object = element.getArguments()[0];
    if (!Node.isObjectLiteralExpression(object)) {
      return;
    }
    const initializer = (name: string) => {
      const property = object.getProperty(name);
      return Node.isPropertyAssignment(property) ? property.getInitializer() : undefined;
    };
    const value = (name: string) => initializer(name)?.getText();
    const stringValue = (name: string) => {
      const expression = initializer(name);
      return Node.isStringLiteral(expression) ? expression.getLiteralValue() : undefined;
    };
    const matches = graph.routes
      .map((route, index) => ({ route, index }))
      .filter(
        ({ route, index }) =>
          (!directOnly || index === sourceIndex) &&
          participants[route.source]?.expression === value("from") &&
          participants[route.target]?.expression === value("to") &&
          route.label === stringValue("label") &&
          route.conditional === (value("when") !== undefined) &&
          (route.outcome === undefined
            ? value("outcome") === undefined
            : route.outcome === stringValue("outcome")),
      );
    if (matches.length !== 1 || routes[matches[0]!.index]!.editable) {
      return;
    }
    routes[matches[0]!.index] = {
      editable: true,
      sourceIndex,
      file: element.getSourceFile().getFilePath(),
      line: element.getStartLineNumber(),
      ...(value("when") ? { predicate: value("when") } : {}),
    };
  });
  const state = locateStateSchema(owner.object);
  return {
    routeArray: {
      file: routeArrayFile,
      line: routeArrayLine,
      length: elements.length,
    },
    routes,
    state,
    participants,
  };
}
function locateStateSchema(object: ObjectLiteralExpression): SourceOwnership["state"] {
  const property = object.getProperty("state");
  if (!Node.isPropertyAssignment(property)) {
    return { reason: "The pipeline state schema is not a direct property assignment." };
  }
  const initializer = property.getInitializer();
  if (Node.isCallExpression(initializer)) {
    return {
      file: initializer.getSourceFile().getFilePath(),
      line: initializer.getStartLineNumber(),
    };
  }
  if (Node.isIdentifier(initializer)) {
    const symbol = initializer.getSymbol();
    const declarations = (symbol?.getAliasedSymbol() ?? symbol)
      ?.getDeclarations()
      .filter(Node.isVariableDeclaration);
    if (declarations?.length === 1) {
      return {
        file: declarations[0]!.getSourceFile().getFilePath(),
        line: declarations[0]!.getStartLineNumber(),
      };
    }
  }
  return {
    reason:
      "The state schema binding is aliased, helper-generated, or ambiguous; open it manually.",
  };
}
function locateParticipants(
  object: ObjectLiteralExpression,
  graph: PipelineInspection,
): Record<string, SourceReference> {
  const semanticParticipants = graph.nodes.flatMap((node) => [
    node,
    ...(node.branches?.map((branch) => branch.participant) ?? []),
  ]);
  const result: Record<string, SourceReference> = Object.fromEntries(
    semanticParticipants.map((node) => [
      node.id,
      {
        editable: false,
        reason: "The participant is not a unique direct binding in the pipeline nodes array.",
      } satisfies SourceReference,
    ]),
  );
  const property = object.getProperty("nodes");
  if (!Node.isPropertyAssignment(property)) {
    return result;
  }
  const initializer = property.getInitializer();
  if (!Node.isArrayLiteralExpression(initializer)) {
    return result;
  }
  const elements = initializer.getElements();
  if (elements.length !== graph.nodes.length) {
    return result;
  }
  elements.forEach((expression, index) => {
    const semantic = graph.nodes[index]!;
    if (!isSafeReference(expression)) {
      return;
    }
    const symbol = expression.getSymbol();
    const declarations = (symbol?.getAliasedSymbol() ?? symbol)?.getDeclarations() ?? [];
    if (declarations.length !== 1) {
      return;
    }
    const declaration = declarations[0]!;
    result[semantic.id] = participantReference(expression, declaration, semantic.kind);
    locateParallelBranchParticipants(declaration, semantic, result);
  });
  return result;
}
function participantReference(
  expression: Expression,
  declaration: import("ts-morph").Node,
  kind: PipelineInspection["nodes"][number]["kind"],
): SourceReference {
  return {
    editable: true,
    expression: expression.getText(),
    file: declaration.getSourceFile().getFilePath(),
    line: declaration.getStartLineNumber(),
    callbacks: callbackLocations(declaration, kind),
  };
}
function locateParallelBranchParticipants(
  declaration: import("ts-morph").Node,
  semantic: PipelineInspection["nodes"][number],
  result: Record<string, SourceReference>,
): void {
  if (!semantic.branches || !Node.isVariableDeclaration(declaration)) {
    return;
  }
  const call = declaration.getInitializer();
  if (!Node.isCallExpression(call)) {
    return;
  }
  const definition = call.getArguments()[0];
  if (!Node.isObjectLiteralExpression(definition)) {
    return;
  }
  const branchesProperty = definition.getProperty("branches");
  const branches = Node.isPropertyAssignment(branchesProperty)
    ? branchesProperty.getInitializer()
    : undefined;
  if (!Node.isObjectLiteralExpression(branches)) {
    return;
  }
  for (const branch of semantic.branches) {
    const property = branches.getProperty(branch.id);
    const expression = Node.isPropertyAssignment(property) ? property.getInitializer() : undefined;
    if (!expression || !isSafeReference(expression)) {
      continue;
    }
    const symbol = expression.getSymbol();
    const declarations = (symbol?.getAliasedSymbol() ?? symbol)?.getDeclarations() ?? [];
    if (declarations.length !== 1) {
      continue;
    }
    result[branch.participant.id] = participantReference(
      expression,
      declarations[0]!,
      branch.participant.kind,
    );
  }
}
function callbackLocations(
  declaration: import("ts-morph").Node,
  kind: PipelineInspection["nodes"][number]["kind"],
): Readonly<Record<string, { readonly file: string; readonly line: number }>> {
  if (!Node.isVariableDeclaration(declaration)) {
    return {};
  }
  const call = declaration.getInitializer();
  if (!Node.isCallExpression(call)) {
    return {};
  }
  const object = call.getArguments()[0];
  if (!Node.isObjectLiteralExpression(object)) {
    return {};
  }
  const names =
    kind === "stage"
      ? ["execute"]
      : kind === "interaction"
        ? ["request", "apply"]
        : kind === "agent"
          ? ["message"]
          : kind === "parallel"
            ? ["merge"]
            : ["summary"];
  return Object.fromEntries(
    names.flatMap((name) => {
      const property = object.getProperty(name);
      if (!Node.isPropertyAssignment(property) && !Node.isMethodDeclaration(property)) {
        return [];
      }
      return [
        [
          name,
          { file: property.getSourceFile().getFilePath(), line: property.getStartLineNumber() },
        ],
      ];
    }),
  );
}
function isSafeReference(expression: Expression): boolean {
  return Node.isIdentifier(expression);
}
function unreadable(
  graph: PipelineInspection,
  reason: string,
  location?: { readonly file: string; readonly line: number },
): SourceOwnership {
  return {
    routes: graph.routes.map(() => ({ editable: false, reason, ...location })),
    participants: Object.fromEntries(
      graph.nodes
        .flatMap((node) => [node, ...(node.branches?.map((branch) => branch.participant) ?? [])])
        .map((node) => [node.id, { editable: false, reason }]),
    ),
  };
}
