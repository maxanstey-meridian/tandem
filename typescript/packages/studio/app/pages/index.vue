<script setup lang="ts">
import { Background } from "@vue-flow/background";
import { Controls } from "@vue-flow/controls";
import {
  VueFlow,
  useVueFlow,
  type Node as FlowNode,
  type Edge,
  type Connection,
} from "@vue-flow/core";
import { MiniMap } from "@vue-flow/minimap";
import ELK from "elkjs/lib/elk.bundled.js";
import { mapElkLayout } from "../../src/elk-geometry";
import {
  positionsForIncomingIdentity,
  ReloadGeneration,
  restoreIncomingPositions,
} from "../../src/presentation";
import {
  elkGraph,
  modePositionStorageKey,
  projectSemanticGraph,
  type PresentationMode,
} from "../../src/semantic-graph";
import SemanticEdge from "../components/SemanticEdge.vue";
import SemanticNode from "../components/SemanticNode.vue";
type Graph = any;
type Ownership = any;
const graph = ref<Graph>();
const loaded = ref<any>();
const mode = ref<PresentationMode>("lifecycle");
const dominantJourneyStatus = ref<"successful-completion" | "best-effort">();
const ownership = ref<Ownership>();
const editing = ref<any>();
const editRevision = ref("");
const config = ref("");
const nodes = ref<FlowNode[]>([]),
  edges = ref<Edge[]>([]);
const diagnostic = ref(""),
  saving = ref(false);
const selected = ref<{ kind: "pipeline" | "node" | "route"; value: any }>({
  kind: "pipeline",
  value: null,
});
const draft = ref<any>();
const { fitView } = useVueFlow();
let timer: any;
let signature = "";
const reloads = new ReloadGeneration();
let sourceGeneration = -1;
let checkingChanges = false;
const storageKey = computed(() =>
  graph.value ? modePositionStorageKey(config.value, graph.value.name, mode.value) : "",
);
const positions = () => Object.fromEntries(nodes.value.map((node) => [node.id, node.position]));
function savePositions() {
  if (storageKey.value) {
    localStorage.setItem(storageKey.value, JSON.stringify(positions()));
  }
}
function clearRoutedGeometry() {
  edges.value = edges.value.map((edge) => ({
    ...edge,
    data: { ...edge.data, elkPath: undefined, elkAnchor: undefined },
  }));
}
function positionsMatch(
  actual: { x: number; y: number } | undefined,
  automatic: { x: number; y: number } | undefined,
) {
  return actual?.x === automatic?.x && actual?.y === automatic?.y;
}
function retainRoutedGeometry(
  retained: Record<string, { x: number; y: number }>,
  automatic: Record<string, { x: number; y: number }>,
) {
  return Object.keys(automatic).every((id) => positionsMatch(retained[id], automatic[id]));
}
function manualPositioningFinished() {
  clearRoutedGeometry();
  savePositions();
}
async function automaticPositions(g: Graph) {
  const elk = new ELK();
  const result = await elk.layout(elkGraph(projectSemanticGraph(g, mode.value)) as any);
  return mapElkLayout(result);
}
async function publish(result: any, force = false, retainLivePositions = true) {
  const nextSignature = JSON.stringify(result.graph);
  const topologyUnchanged = nextSignature === signature;
  const previousOwnership = ownership.value;
  const current = positionsForIncomingIdentity(
    config.value,
    graph.value?.name,
    result.config,
    result.graph.name,
    retainLivePositions ? positions() : {},
  );
  const layout = await automaticPositions(result.graph);
  const retained = restoreIncomingPositions(
    result.config,
    result.graph.name,
    result.graph.nodes.map((node: any) => node.id),
    current,
    layout.positions,
    (key) => localStorage.getItem(key),
    modePositionStorageKey(result.config, result.graph.name, mode.value),
  );
  graph.value = result.graph;
  loaded.value = result;
  const projection = projectSemanticGraph(result.graph, mode.value);
  dominantJourneyStatus.value = projection.dominantJourneyStatus;
  ownership.value = result.ownership;
  editing.value = result.editing;
  editRevision.value = result.editRevision;
  config.value = result.config;
  const keepElkPaths = retainRoutedGeometry(retained, layout.positions);
  nodes.value = projection.nodes.map((node: any) => ({
    id: node.id,
    position: retained[node.id] ?? layout.positions[node.id] ?? { x: 0, y: 0 },
    data: {
      ...node,
      start: node.id === result.graph.start,
      selectRoute: selectRouteById,
      hoverRoute: hoverRouteById,
      restoreEmphasis,
    },
    type: "semantic",
    class: `kind-${node.kind}`,
  }));
  edges.value = projection.routes
    .filter((route) => route.visible)
    .map((route: any) => ({
      id: route.id,
      source: route.source,
      sourceHandle: route.sourcePort,
      target: route.target,
      targetHandle: route.targetPort,
      type: "semantic",
      data: {
        ...route,
        elkPath: keepElkPaths ? layout.edgePaths[route.id] : undefined,
        elkAnchor: keepElkPaths ? layout.edgeAnchors[route.id] : undefined,
        focused: false,
        unrelated: false,
        normallyVisible: route.visible,
      },
      markerEnd: "arrowclosed",
      updatable: result.ownership.routes[route.order]?.editable,
    }));
  reconcileSelection(projection, result.graph, topologyUnchanged, previousOwnership);
  signature = nextSignature;
  savePositions();
  if (force || !retained[graph.value.start]) {
    await nextTick();
    fitView();
  }
}
async function reload(force = false) {
  const generation = reloads.begin();
  const result: any = await $fetch("/api/graph");
  if (!reloads.isCurrent(generation)) {
    return;
  }
  if (!result.ok) {
    diagnostic.value = result.error;
    return;
  }
  diagnostic.value = "";
  if (
    force ||
    JSON.stringify(result.graph) !== signature ||
    JSON.stringify(result.ownership) !== JSON.stringify(ownership.value)
  ) {
    try {
      await publish(result, force);
    } catch (error) {
      diagnostic.value = `Automatic layout failed: ${error instanceof Error ? error.message : String(error)}`;
    }
  }
}
function nodeKind(id: string) {
  return graph.value?.nodes.find((node: any) => node.id === id)?.kind;
}
function needsOutcome(id: string) {
  return ["agent", "parallel"].includes(nodeKind(id));
}
function groupKey(source: string, outcome?: string | null) {
  return `${source}\u0000${outcome ?? "default"}`;
}
function outgoingOrder(route: any) {
  return graph.value.routes
    .filter(
      (candidate: any) => candidate.source === route.source && candidate.outcome === route.outcome,
    )
    .findIndex((candidate: any) => candidate.id === route.id);
}
function changeDraftSource() {
  draft.value.outcome = needsOutcome(draft.value.from) ? (draft.value.outcome ?? "success") : null;
  resetInsertionOrder();
}
function resetInsertionOrder() {
  if (draft.value?.kind !== "insert") {
    return;
  }
  const options = editing.value.insertions[groupKey(draft.value.from, draft.value.outcome)] ?? [];
  draft.value.order = options.findIndex(Boolean);
}
function emphasize(routeId?: string, nodeId?: string) {
  const route = graph.value?.routes.find((item: any) => item.id === routeId);
  edges.value = edges.value.map((edge) => ({
    ...edge,
    data: {
      ...edge.data,
      focused: edge.id === routeId,
      unrelated:
        Boolean(routeId || nodeId) &&
        edge.id !== routeId &&
        edge.source !== nodeId &&
        edge.target !== nodeId,
    },
  }));
  nodes.value = nodes.value.map((node) => ({
    ...node,
    class: [
      `kind-${node.data.kind}`,
      {
        unrelated:
          Boolean(routeId || nodeId) &&
          node.id !== nodeId &&
          node.id !== route?.source &&
          node.id !== route?.target,
      },
    ],
  }));
}
function compactUnfocusedFailures(exceptId?: string) {
  edges.value = edges.value.filter(
    (edge) => edge.id === exceptId || edge.data?.normallyVisible !== false,
  );
}
function revealCompactedRoute(id: string) {
  if (edges.value.some((edge) => edge.id === id) || !graph.value || !loaded.value) {
    return;
  }
  const route = projectSemanticGraph(graph.value, mode.value).routes.find(
    (candidate) => candidate.id === id,
  );
  if (!route?.compacted) {
    return;
  }
  edges.value = [
    ...edges.value,
    {
      id: route.id,
      source: route.source,
      sourceHandle: route.sourcePort,
      target: route.target,
      targetHandle: route.targetPort,
      type: "semantic",
      data: {
        ...route,
        elkPath: undefined,
        focused: false,
        unrelated: false,
        normallyVisible: false,
      },
      markerEnd: "arrowclosed",
      updatable: loaded.value.ownership.routes[route.order]?.editable,
    },
  ];
}
function reconcileSelection(
  projection: ReturnType<typeof projectSemanticGraph>,
  nextGraph: Graph,
  topologyUnchanged: boolean,
  previousOwnership: Ownership,
) {
  if (selected.value.kind === "route" && selected.value.value) {
    const prior = selected.value.value;
    const priorPredicate = previousOwnership?.routes[prior.order]?.predicate ?? null;
    const matches = topologyUnchanged
      ? projection.routes.filter((candidate) => candidate.id === prior.id)
      : projection.routes.filter(
          (candidate) =>
            candidate.source === prior.source &&
            candidate.target === prior.target &&
            candidate.label === prior.label &&
            candidate.outcome === prior.outcome &&
            candidate.conditional === prior.conditional &&
            (ownership.value.routes[candidate.order]?.predicate ?? null) === priorPredicate,
        );
    if (matches.length === 1) {
      const route = matches[0];
      selected.value = { kind: "route", value: route };
      if (!topologyUnchanged && draft.value?.kind === "update") {
        draft.value = {
          ...draft.value,
          order: route.order,
          originalOrder: route.order,
          groupOrder: outgoingOrder(route),
        };
      }
    } else {
      selected.value = { kind: "pipeline", value: nextGraph };
      draft.value = undefined;
    }
  } else if (selected.value.kind === "node") {
    const node = projection.nodes.find((candidate) => candidate.id === selected.value.value?.id);
    if (node) {
      selected.value = { kind: "node", value: node };
    } else {
      selected.value = { kind: "pipeline", value: nextGraph };
      draft.value = undefined;
    }
  } else if (selected.value.kind === "pipeline") {
    selected.value = { kind: "pipeline", value: nextGraph };
  }
  restoreEmphasis();
}
function restoreEmphasis() {
  const selectedRouteId = selected.value.kind === "route" ? selected.value.value?.id : undefined;
  if (selectedRouteId) {
    revealCompactedRoute(selectedRouteId);
  }
  compactUnfocusedFailures(selectedRouteId);
  if (selected.value.kind === "route") {
    emphasize(selectedRouteId);
  } else if (selected.value.kind === "node") {
    emphasize(undefined, selected.value.value?.id);
  } else {
    emphasize();
  }
}
function hoverRouteById(id: string) {
  revealCompactedRoute(id);
  emphasize(id);
}
function hoverRoute(event: any) {
  emphasize(event.edge.id);
}
function hoverNode(event: any) {
  compactUnfocusedFailures();
  emphasize(undefined, event.node.id);
}
function selectNode(event: any) {
  compactUnfocusedFailures();
  selected.value = { kind: "node", value: event.node.data };
  emphasize(undefined, event.node.id);
  draft.value = undefined;
}
function selectRouteById(id: string) {
  revealCompactedRoute(id);
  const edge = edges.value.find((item) => item.id === id);
  if (edge) {
    selectRoute({ edge });
  } else {
    const route = graph.value.routes.find((item: any) => item.id === id);
    if (route) {
      selectRoute({ edge: { data: route } });
    }
  }
}
function selectRoute(event: any) {
  const route = event.edge.data,
    owner = ownership.value.routes[route.order];
  compactUnfocusedFailures(route.id);
  revealCompactedRoute(route.id);
  selected.value = { kind: "route", value: route };
  emphasize(route.id);
  draft.value = owner.editable
    ? {
        kind: "update",
        order: route.order,
        originalOrder: route.order,
        groupOrder: outgoingOrder(route),
        from: route.source,
        to: route.target,
        label: route.label,
        outcome: route.outcome ?? null,
        when: owner.predicate ?? null,
      }
    : undefined;
}
function portOutcome(source: string | null | undefined, handle: string | null | undefined) {
  const node = nodes.value.find((candidate) => candidate.id === source);
  const port = [...(node?.data.ports ?? []), ...(node?.data.creationPorts ?? [])].find(
    (candidate: any) => candidate.id === handle,
  );
  return port?.outcome;
}
function insertionOrder(
  source: string,
  outcome: "success" | "failed" | undefined,
  handle: string | null | undefined,
  options: readonly boolean[],
) {
  const node = nodes.value.find((candidate) => candidate.id === source);
  const port = node?.data.ports.find((candidate: any) => candidate.id === handle);
  const route = graph.value.routes.find((candidate: any) => candidate.id === port?.routeId);
  const afterRoute = route?.outcome === outcome ? outgoingOrder(route) + 1 : -1;
  return options[afterRoute] ? afterRoute : options.findIndex(Boolean);
}
function connect(connection: Connection) {
  const outcome = needsOutcome(String(connection.source))
    ? (portOutcome(String(connection.source), connection.sourceHandle) ?? "success")
    : undefined;
  const options = editing.value.insertions[groupKey(String(connection.source), outcome)] ?? [];
  if (!options.some(Boolean)) {
    diagnostic.value =
      "Route creation is read-only because no unambiguous source boundary is available.";
    return;
  }
  draft.value = {
    kind: "insert",
    order: insertionOrder(String(connection.source), outcome, connection.sourceHandle, options),
    from: connection.source,
    to: connection.target,
    label: "",
    outcome,
    when: null,
  };
  selected.value = { kind: "route", value: null };
}
function reconnect(event: any) {
  const route = event.edge.data,
    owner = ownership.value.routes[route.order];
  if (!owner.editable) {
    diagnostic.value = owner.reason;
    return;
  }
  draft.value = {
    kind: "update",
    order: route.order,
    originalOrder: route.order,
    groupOrder: outgoingOrder(route),
    from: event.connection.source,
    to: event.connection.target,
    label: route.label,
    outcome: needsOutcome(event.connection.source)
      ? (portOutcome(event.connection.source, event.connection.sourceHandle) ??
        route.outcome ??
        "success")
      : null,
    when: owner.predicate ?? null,
  };
}
async function submit(edit: any) {
  saving.value = true;
  diagnostic.value = "";
  const body =
    edit.kind === "insert"
      ? {
          kind: "insert",
          order: edit.order,
          from: edit.from,
          to: edit.to,
          label: edit.label,
          outcome: edit.outcome ?? undefined,
          when: edit.when || undefined,
        }
      : {
          kind: "update",
          order: edit.originalOrder,
          from: edit.from,
          to: edit.to,
          label: edit.label,
          outcome: edit.outcome ?? null,
          when: edit.when || null,
        };
  try {
    const result: any = await $fetch("/api/routes", {
      method: "POST",
      body: { edit: body, editRevision: editRevision.value },
    });
    if (!result.ok) {
      diagnostic.value = result.error;
      return;
    }
    draft.value = undefined;
    await reload(true);
  } finally {
    saving.value = false;
  }
}
function remove() {
  if (draft.value?.kind === "update") {
    void submitRaw({ kind: "delete", order: draft.value.originalOrder });
  }
}
function move(delta: number) {
  if (draft.value?.kind === "update") {
    void submitRaw({
      kind: "move",
      order: draft.value.originalOrder,
      toOrder: draft.value.groupOrder + delta,
    });
  }
}
async function submitRaw(body: any) {
  saving.value = true;
  try {
    const result: any = await $fetch("/api/routes", {
      method: "POST",
      body: { edit: body, editRevision: editRevision.value },
    });
    if (!result.ok) {
      diagnostic.value = result.error;
      return;
    }
    draft.value = undefined;
    await reload(true);
  } finally {
    saving.value = false;
  }
}
async function setMode(next: PresentationMode) {
  if (next === mode.value || !loaded.value) {
    return;
  }
  savePositions();
  const previous = {
    mode: mode.value,
    dominantJourneyStatus: dominantJourneyStatus.value,
    graph: graph.value,
    loaded: loaded.value,
    ownership: ownership.value,
    editing: editing.value,
    editRevision: editRevision.value,
    config: config.value,
    nodes: nodes.value,
    edges: edges.value,
    signature,
  };
  mode.value = next;
  try {
    await publish(loaded.value, true, false);
    diagnostic.value = "";
  } catch (error) {
    mode.value = previous.mode;
    dominantJourneyStatus.value = previous.dominantJourneyStatus;
    graph.value = previous.graph;
    loaded.value = previous.loaded;
    ownership.value = previous.ownership;
    editing.value = previous.editing;
    editRevision.value = previous.editRevision;
    config.value = previous.config;
    nodes.value = previous.nodes;
    edges.value = previous.edges;
    signature = previous.signature;
    diagnostic.value = `Automatic layout failed: ${error instanceof Error ? error.message : String(error)}`;
  }
}
async function relayout() {
  try {
    const layout = await automaticPositions(graph.value);
    nodes.value = nodes.value.map((node) => ({ ...node, position: layout.positions[node.id] }));
    edges.value = edges.value.map((edge) => ({
      ...edge,
      data: {
        ...edge.data,
        elkPath: layout.edgePaths[edge.id],
        elkAnchor: layout.edgeAnchors[edge.id],
      },
    }));
    diagnostic.value = "";
    savePositions();
    await nextTick();
    fitView();
  } catch (error) {
    diagnostic.value = `Automatic layout failed: ${error instanceof Error ? error.message : String(error)}`;
  }
}
function location(value: any) {
  return value?.file ? `${value.file}:${value.line}` : "Source location unavailable";
}
async function openSource(target: any) {
  const result: any = await $fetch("/api/open", { method: "POST", body: target });
  if (!result.ok) {
    diagnostic.value = result.error;
  }
}
function persistence(node: any) {
  if (node.persist === true) {
    return "explicitly enabled for this participant";
  }
  if (node.persist === false) {
    return "explicitly disabled for this participant";
  }
  return graph.value.persist
    ? "inherited from pipeline: enabled"
    : "inherited from pipeline: disabled";
}
function kindLabel(kind: string) {
  return (
    {
      stage: "Stage",
      interaction: "Interaction",
      agent: "Agent",
      parallel: "Parallel group",
      completion: "Successful output",
      failure: "Failed output",
    } as Record<string, string>
  )[kind];
}
function role(kind: string) {
  return (
    {
      stage: "Transforms pipeline state",
      interaction: "Pauses for an external response",
      agent: "Uses a model with granted capabilities",
      parallel: "Runs owned branches and merges their state",
      completion: "Successful pipeline output",
      failure: "Failed pipeline output",
    } as any
  )[kind];
}
async function checkChanges() {
  if (checkingChanges) {
    return;
  }
  checkingChanges = true;
  try {
    const result: any = await $fetch("/api/changes");
    if (sourceGeneration < 0) {
      sourceGeneration = result.generation;
    } else if (result.generation > sourceGeneration) {
      sourceGeneration = result.generation;
      await reload();
    }
  } finally {
    checkingChanges = false;
  }
}
onMounted(() => {
  void reload(true);
  void checkChanges();
  timer = setInterval(() => void checkChanges(), 500);
});
onBeforeUnmount(() => clearInterval(timer));
</script>
<template>
  <main>
    <header>
      <div>
        <h1>Tandem Studio</h1>
        <p>
          Code is authoritative. Routes are attempted in displayed order; Studio never executes the
          pipeline.
        </p>
      </div>
      <div class="toolbar">
        <div class="mode-switch" role="group" aria-label="Graph presentation">
          <button :aria-pressed="mode === 'lifecycle'" @click="setMode('lifecycle')">
            Lifecycle</button
          ><button :aria-pressed="mode === 'all'" @click="setMode('all')">All routes</button>
        </div>
        <button @click="reload(true)">Reload</button
        ><button :disabled="!graph" @click="relayout">Re-layout</button>
      </div>
    </header>
    <p v-if="diagnostic" class="diagnostic">
      <strong v-if="graph">Last valid graph retained.</strong>
      <strong v-else>Studio could not load a pipeline.</strong> {{ diagnostic }}
    </p>
    <p
      v-if="mode === 'lifecycle' && dominantJourneyStatus === 'best-effort'"
      class="journey-notice"
    >
      No successful completion is reachable without a failure path. The highlighted journey is
      best-effort progression only.
    </p>
    <section>
      <VueFlow
        v-model:nodes="nodes"
        v-model:edges="edges"
        fit-view-on-init
        :delete-key-code="null"
        @node-drag-stop="manualPositioningFinished"
        @node-click="selectNode"
        @node-mouse-enter="hoverNode"
        @node-mouse-leave="restoreEmphasis"
        @edge-click="selectRoute"
        @edge-mouse-enter="hoverRoute"
        @edge-mouse-leave="restoreEmphasis"
        @connect="connect"
        @edge-update="reconnect"
        ><template #node-semantic="slotProps"><SemanticNode v-bind="slotProps" /></template
        ><template #edge-semantic="slotProps"><SemanticEdge v-bind="slotProps" /></template>
        <Background /><MiniMap /><Controls
      /></VueFlow>
      <aside>
        <template v-if="draft"
          ><h2>{{ draft.kind === "insert" ? "Create route" : "Edit route" }}</h2>
          <div v-if="draft.kind === 'update' && selected.value" class="route-facts">
            <p>
              Source: <strong>{{ selected.value.source }}</strong> · Target:
              <strong>{{ selected.value.target }}</strong>
            </p>
            <p>
              Grouped priority: {{ outgoingOrder(selected.value) + 1 }} · Outcome:
              {{ selected.value.outcome ?? "ordinary" }} · Conditional:
              {{ selected.value.conditional ? "yes" : "no" }}
            </p>
            <p>
              Exact predicate source:
              <code>{{ ownership.routes[selected.value.order]?.predicate ?? "none" }}</code>
            </p>
            <p>
              Editability: editable · Full source location:
              {{ location(ownership.routes[selected.value.order]) }}
            </p>
          </div>
          <label
            >From<select v-model="draft.from" @change="changeDraftSource">
              <option
                v-for="node in graph.nodes"
                :key="node.id"
                :disabled="['completion', 'failure'].includes(node.kind)"
                :value="node.id"
              >
                {{ node.id }} · {{ node.kind }}
              </option>
            </select></label
          ><label
            >To<select v-model="draft.to">
              <option v-for="node in graph.nodes" :key="node.id" :value="node.id">
                {{ node.id }} · {{ node.kind }}
              </option>
            </select></label
          >
          <fieldset v-if="needsOutcome(draft.from)">
            <legend>What happened?</legend>
            <label
              ><input
                v-model="draft.outcome"
                type="radio"
                value="success"
                @change="resetInsertionOrder"
              />Succeeded</label
            ><label
              ><input
                v-model="draft.outcome"
                type="radio"
                value="failed"
                @change="resetInsertionOrder"
              />Failed</label
            >
          </fieldset>
          <label
            >Meaning<input
              v-model="draft.label"
              placeholder="Describe this possible next step" /></label
          ><label
            >Optional state condition<PredicateEditor v-model="draft.when" /><small
              >Ordinary TypeScript, e.g. (state) =&gt; state.review?.decision === "Accept"</small
            ></label
          ><label v-if="draft.kind === 'insert'"
            >Route order<select v-model.number="draft.order">
              <option
                v-for="(_, boundary) in editing.insertions[groupKey(draft.from, draft.outcome)] ??
                []"
                v-show="editing.insertions[groupKey(draft.from, draft.outcome)][boundary]"
                :key="boundary"
                :value="boundary"
              >
                {{ boundary + 1 }}
              </option>
            </select></label
          >
          <p v-else>Outgoing route priority: {{ draft.groupOrder + 1 }}</p>
          <div class="actions">
            <button :disabled="saving" @click="submit(draft)">Validate and save code</button
            ><button v-if="draft.kind === 'update'" :disabled="saving" @click="remove">
              Delete</button
            ><button
              v-if="
                draft.kind === 'update' &&
                draft.groupOrder > 0 &&
                editing.moves[draft.originalOrder][draft.groupOrder - 1]
              "
              :disabled="saving"
              @click="move(-1)"
            >
              Earlier</button
            ><button
              v-if="
                draft.kind === 'update' &&
                draft.groupOrder < editing.moves[draft.originalOrder].length - 1 &&
                editing.moves[draft.originalOrder][draft.groupOrder + 1]
              "
              :disabled="saving"
              @click="move(1)"
            >
              Later
            </button>
          </div>
          <p>
            <small
              >Changes are formatted, typechecked, reconstructed, and shown only after validation
              succeeds.</small
            >
          </p></template
        >
        <template v-else-if="selected.kind === 'node'"
          ><h2>{{ selected.value.id }}</h2>
          <p class="role">{{ role(selected.value.kind) }}</p>
          <p v-if="selected.value.id === graph.start"><strong>Execution begins here.</strong></p>
          <p>Persistence: {{ persistence(selected.value) }}</p>
          <template v-if="selected.value.agent"
            ><h3>Agent capabilities</h3>
            <ul>
              <li v-for="capability in selected.value.agent.capabilities" :key="capability.name">
                <strong>{{ capability.name }}</strong>
                <pre>{{ JSON.stringify(capability.requestSchema, null, 2) }}</pre>
              </li>
            </ul>
            <template v-if="selected.value.agent.outputSchema">
              <h3>Structured output shape</h3>
              <pre>{{ JSON.stringify(selected.value.agent.outputSchema, null, 2) }}</pre>
            </template>
            <p v-else>Structured output: none</p>
            <p>
              Workspace tools: {{ selected.value.agent.workspace ? "enabled" : "none" }}
            </p></template
          ><template v-if="selected.value.interaction"
            ><h3>Interaction request shape</h3>
            <pre>{{ JSON.stringify(selected.value.interaction.requestSchema, null, 2) }}</pre>
            <h3>Response shape</h3>
            <pre>{{
              JSON.stringify(selected.value.interaction.responseSchema, null, 2)
            }}</pre></template
          ><template v-if="selected.value.branches"
            ><h3>Parallel branches</h3>
            <ul>
              <li v-for="branch in selected.value.branches" :key="branch.id">
                {{ branch.id }} → {{ branch.participant.id }} ({{
                  kindLabel(branch.participant.kind)
                }})
                <button
                  v-if="ownership.participants[branch.participant.id]?.file"
                  @click="openSource({ kind: 'participant', id: branch.participant.id })"
                >
                  Open declaration
                </button>
                <ul v-if="ownership.participants[branch.participant.id]?.callbacks">
                  <li
                    v-for="(callback, name) in ownership.participants[branch.participant.id]
                      .callbacks"
                    :key="name"
                  >
                    {{ name }} — {{ location(callback) }}
                    <button
                      @click="openSource({ kind: 'callback', id: branch.participant.id, name })"
                    >
                      Open callback
                    </button>
                  </li>
                </ul>
              </li>
            </ul></template
          >
          <p>
            <small>{{ location(ownership.participants[selected.value.id]) }}</small>
            <button
              v-if="ownership.participants[selected.value.id]?.file"
              @click="openSource({ kind: 'participant', id: selected.value.id })"
            >
              Open declaration
            </button>
          </p>
          <template v-if="ownership.participants[selected.value.id]?.callbacks">
            <h3>Callback source</h3>
            <ul>
              <li
                v-for="(callback, name) in ownership.participants[selected.value.id].callbacks"
                :key="name"
              >
                {{ name }} — {{ location(callback) }}
                <button @click="openSource({ kind: 'callback', id: selected.value.id, name })">
                  Open
                </button>
              </li>
            </ul>
          </template></template
        >
        <template v-else-if="selected.kind === 'route' && selected.value"
          ><h2>{{ selected.value.label }}</h2>
          <p>
            After <strong>{{ selected.value.source }}</strong
            >, continue to <strong>{{ selected.value.target }}</strong
            >.
          </p>
          <p>
            Priority {{ outgoingOrder(selected.value) + 1 }} for this participant and outcome.
            {{
              selected.value.conditional
                ? "Taken only when its TypeScript state predicate is true."
                : "Unconditional for this outcome."
            }}
          </p>
          <p>Participant outcome: {{ selected.value.outcome ?? "ordinary" }}</p>
          <p>
            Exact predicate source:
            <code>{{ ownership.routes[selected.value.order]?.predicate ?? "none" }}</code>
          </p>
          <p>
            Editability:
            {{
              ownership.routes[selected.value.order]?.editable
                ? "editable"
                : `read-only — ${ownership.routes[selected.value.order]?.reason}`
            }}
          </p>
          <p>Full source location: {{ location(ownership.routes[selected.value.order]) }}</p>
          <button
            v-if="ownership.routes[selected.value.order]?.file"
            @click="openSource({ kind: 'route', order: selected.value.order })"
          >
            Open route source
          </button>
          <p v-if="!ownership.routes[selected.value.order]?.editable" class="readonly">
            Read-only: {{ ownership.routes[selected.value.order]?.reason }}<br /><small>{{
              location(ownership.routes[selected.value.order])
            }}</small>
          </p></template
        >
        <template v-else-if="graph"
          ><h2>{{ graph.name }}</h2>
          <p>
            Execution begins at <strong>{{ graph.start }}</strong> and may finish at
            {{ graph.outputs.join(", ") }}.
          </p>
          <p>Pipeline persistence: {{ graph.persist ? "enabled" : "participant opt-in only" }}</p>
          <h3>State shape</h3>
          <pre>{{ JSON.stringify(graph.stateSchema, null, 2) }}</pre>
          <p>
            <small>{{ location(ownership.state) }}</small>
            <button v-if="ownership.state?.file" @click="openSource({ kind: 'state' })">
              Open state schema
            </button>
            <span v-else-if="ownership.state?.reason"> {{ ownership.state.reason }}</span>
          </p></template
        >
      </aside>
    </section>
  </main>
</template>
<style scoped>
main {
  height: 100vh;
  font: 14px system-ui;
  color: #172033;
  background: #f6f8fb;
}
header {
  height: 88px;
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 0 20px;
  border-bottom: 1px solid #d9deea;
}
header div {
  flex: 1;
}
h1,
p {
  margin: 3px 0;
}
button,
input,
select {
  padding: 8px;
}
section {
  display: grid;
  grid-template-columns: 1fr 340px;
  height: calc(100vh - 89px);
}
aside {
  overflow: auto;
  padding: 18px;
  border-left: 1px solid #d9deea;
  background: white;
}
pre {
  white-space: pre-wrap;
  font-size: 12px;
}
.diagnostic,
.readonly {
  padding: 10px;
  background: #fff0f0;
  color: #8b1721;
}
.vue-flow {
  background: #eef2f8;
}
label {
  display: grid;
  gap: 5px;
  margin: 12px 0;
}
fieldset label {
  display: inline-flex;
  margin-right: 14px;
}
.actions {
  display: flex;
  gap: 7px;
  flex-wrap: wrap;
}
.role {
  font-size: 16px;
}
.toolbar,
.mode-switch {
  display: flex;
  align-items: center;
  gap: 7px;
}
.mode-switch button[aria-pressed="true"] {
  background: #26334c;
  color: white;
}
:deep(.semantic-card) {
  position: relative;
  box-sizing: border-box;
  padding: 12px;
  border: 1px solid #aeb8c9;
  border-left: 5px solid #73809b;
  border-radius: 8px;
  background: white;
  text-align: left;
  box-shadow: 0 2px 7px #26334c18;
}
:deep(.semantic-card header) {
  height: 30px;
  padding: 0;
  border: 0;
  display: grid;
  gap: 2px;
}
:deep(.semantic-card header small),
:deep(.authored-id),
:deep(.route-row small) {
  color: #526078;
  font-size: 11px;
}
:deep(.authored-id) {
  display: block;
  height: 14px;
}
:deep(.start) {
  display: block;
  height: 16px;
  margin: 0;
}
:deep(.outcome-heading) {
  display: flex;
  align-items: center;
  height: 18px;
}
:deep(.route-row) {
  position: relative;
  box-sizing: border-box;
  display: flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  margin: 0 -6px;
  padding: 0 12px 0 6px;
  border-top: 1px solid #e3e7ee;
  cursor: pointer;
}
:deep(.route-row small) {
  margin-left: auto;
}
:deep(.creation-row) {
  position: relative;
  box-sizing: border-box;
  height: 20px;
}
:deep(.route-row.compacted) {
  opacity: 0.72;
  border-left: 3px double #a73541;
}
:deep(.route-correction) {
  stroke-dasharray: 7 5;
}
:deep(.route-failure) {
  stroke-dasharray: 2 5;
}
:deep(.vue-flow__edge-path.route-primary),
:deep(.vue-flow__edge-path.route-terminal) {
  stroke-width: 3;
  stroke: #385d4d;
}
:deep(.vue-flow__edge-path.route-correction) {
  stroke: #735b2e;
  stroke-width: 2;
}
:deep(.vue-flow__edge-path.route-failure) {
  stroke: #9e3540;
}
:deep(.unrelated) {
  opacity: 0.22;
}
:deep(.focused-edge-label) {
  position: absolute;
  max-width: 180px;
  padding: 4px 7px;
  border: 1px solid #aeb8c9;
  border-radius: 4px;
  background: white;
  pointer-events: none;
}
:deep(.semantic-card .vue-flow__handle) {
  width: 8px;
  height: 8px;
}
.start {
  display: inline-block;
  margin-top: 4px;
  font-size: 11px;
  font-weight: 700;
}
.semantic-agent {
  border-color: #6255c7;
}
.semantic-interaction {
  border-color: #b88919;
}
.semantic-parallel {
  border-color: #27845b;
}
.semantic-completion {
  border-color: #21833d;
  background: #e6faeb;
}
.semantic-failure {
  border-color: #bd3441;
  background: #ffeaea;
}
.kind-agent {
  background: #eef0ff;
}
.kind-interaction {
  background: #fff8dd;
}
.kind-parallel {
  background: #eefbf4;
}
.kind-completion {
  background: #e6faeb;
}
.kind-failure {
  background: #ffeaea;
}
@media (max-width: 720px) {
  main {
    height: auto;
    min-height: 100vh;
  }
  header {
    height: auto;
    min-height: 88px;
    padding: 10px;
    flex-wrap: wrap;
  }
  header p {
    display: none;
  }
  section {
    grid-template-columns: 1fr;
    grid-template-rows: minmax(55vh, 1fr) auto;
    height: auto;
    min-height: calc(100vh - 100px);
  }
  .vue-flow {
    min-height: 55vh;
  }
  aside {
    max-height: 45vh;
    border-left: 0;
    border-top: 1px solid #d9deea;
  }
}
</style>
