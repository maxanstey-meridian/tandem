import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { computed, defineComponent, nextTick, onBeforeUnmount, onMounted, ref } from "vue";

const VueFlowStub = defineComponent({
  name: "VueFlow",
  props: {
    nodes: { type: Array, default: () => [] },
    edges: { type: Array, default: () => [] },
    deleteKeyCode: { default: "Backspace" },
  },
  emits: [
    "update:nodes",
    "update:edges",
    "edge-click",
    "node-click",
    "connect",
    "edge-update",
    "node-drag-stop",
    "node-mouse-enter",
    "node-mouse-leave",
    "edge-mouse-enter",
    "edge-mouse-leave",
  ],
  mounted() {
    window.addEventListener("keydown", this.onDelete);
  },
  beforeUnmount() {
    window.removeEventListener("keydown", this.onDelete);
  },
  methods: {
    onDelete(event: KeyboardEvent) {
      if (this.deleteKeyCode !== null && ["Backspace", "Delete"].includes(event.key)) {
        this.$emit("update:edges", []);
      }
    },
  },
  template: `<div data-testid="flow" :data-edge-count="edges.length" :data-edge-target="edges[0]?.target" :data-delete-key="String(deleteKeyCode)" :data-edge-path="edges[0]?.data.elkPath || ''" :data-edge-focused="String(Boolean(edges[0]?.data.focused))" :data-focused-edge="edges.find((edge) => edge.data?.focused)?.id || ''" :data-edge-unrelated="String(Boolean(edges[0]?.data.unrelated))" :data-first-node-position="nodes[0] ? nodes[0].position.x + ',' + nodes[0].position.y : ''">
    <div v-if="edges[0]" data-testid="rendered-edge"><slot name="edge-semantic" v-bind="edges[0]" /></div>
    <button data-testid="select-route" @click="$emit('edge-click', { edge: edges[0] })">select</button>
    <button data-testid="hover-route" @mouseenter="$emit('edge-mouse-enter', { edge: edges[0] })" @mouseleave="$emit('edge-mouse-leave', { edge: edges[0] })">hover route</button>
    <button data-testid="drag-node" @click="$emit('update:nodes', nodes.map((node, index) => index === 0 ? { ...node, position: { x: 37, y: 41 } } : node)); $emit('node-drag-stop', { node: nodes[0] })">drag</button>
    <template v-for="node in nodes" :key="node.id">
      <div :data-testid="'rendered-node-' + node.id"><slot name="node-semantic" :data="node.data" /></div>
      <button :data-testid="'select-node-' + node.id" @click="$emit('node-click', { node })">node</button>
    </template>
    <button data-testid="connect" @click="$emit('connect', { source: 'agent', target: 'done' })">connect</button>
    <button data-testid="connect-after-route" @click="$emit('connect', { source: 'agent', sourceHandle: 'port:agent:success:0', target: 'done' })">connect after route</button>
    <button data-testid="reconnect" @click="$emit('edge-update', { edge: edges[0], connection: { source: 'agent', target: 'agent' } })">reconnect</button>
    <slot />
  </div>`,
});

vi.mock("@vue-flow/core", () => ({
  VueFlow: VueFlowStub,
  Handle: defineComponent({ props: ["id"], template: '<i class="handle" :data-handle-id="id" />' }),
  Position: { Left: "left", Right: "right", Bottom: "bottom" },
  BaseEdge: defineComponent({ template: '<i class="vue-flow__edge-path" />' }),
  EdgeLabelRenderer: defineComponent({ template: "<slot />" }),
  getBezierPath: () => ["M 0 0", 0, 0],
  useVueFlow: () => ({ fitView: vi.fn() }),
}));
vi.mock("@vue-flow/background", () => ({ Background: defineComponent({ template: "<i />" }) }));
vi.mock("@vue-flow/controls", () => ({ Controls: defineComponent({ template: "<i />" }) }));
vi.mock("@vue-flow/minimap", () => ({ MiniMap: defineComponent({ template: "<i />" }) }));
const layoutFailure = vi.hoisted(() => ({ value: false }));
vi.mock("elkjs/lib/elk.bundled.js", () => ({
  default: class {
    async layout(input: { children: { id: string }[]; edges: { id: string }[] }) {
      if (layoutFailure.value) {
        throw new Error("ELK unavailable");
      }
      return {
        children: input.children.map((item, index) => ({ ...item, x: index * 100, y: 0 })),
        edges: input.edges.map((edge) => ({
          ...edge,
          sections: [
            {
              startPoint: { x: 10, y: 10 },
              bendPoints: [
                { x: 10, y: 50 },
                { x: 90, y: 50 },
              ],
              endPoint: { x: 90, y: 90 },
            },
          ],
        })),
      };
    }
  },
}));

let Page: typeof import("../app/pages/index.vue").default;
beforeAll(async () => {
  const values = new Map<string, string>();
  const storage = {
    clear: () => values.clear(),
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => values.set(key, value),
  };
  Object.assign(globalThis, {
    computed,
    localStorage: storage,
    nextTick,
    onBeforeUnmount,
    onMounted,
    ref,
  });
  Page = (await import("../app/pages/index.vue")).default;
});
afterEach(() => {
  vi.restoreAllMocks();
  layoutFailure.value = false;
  localStorage.clear();
});

const route = {
  id: "route:0",
  source: "agent",
  target: "done",
  label: "finish",
  order: 0,
  outcome: "success",
  conditional: false,
};
function response(routes = [route]) {
  return {
    ok: true,
    config: "/project/tandem.config.ts",
    graph: {
      name: "mounted",
      stateSchema: { type: "object" },
      start: "agent",
      persist: true,
      nodes: [
        {
          id: "agent",
          kind: "agent",
          persist: false,
          agent: {
            capabilities: [
              { name: "lookup", requestSchema: { type: "object", required: ["query"] } },
            ],
            outputSchema: { type: "object", required: ["decision"] },
            workspace: false,
          },
        },
        { id: "done", kind: "completion" },
        { id: "enabled", kind: "stage", persist: true },
      ],
      routes,
      outputs: ["done"],
    },
    ownership: {
      routeArray: { file: "/project/pipeline.ts", line: 1, length: routes.length },
      routes: routes.map(() => ({
        editable: true,
        file: "/project/pipeline.ts",
        line: 1,
        sourceIndex: 0,
      })),
      participants: {
        agent: { editable: true },
        done: { editable: true },
        enabled: { editable: true },
      },
    },
    editing: {
      insertions: { "agent\u0000success": [true, true], "agent\u0000failed": [true] },
      moves: routes.map(() => [true]),
    },
    editRevision: `revision-${routes.length}`,
  };
}
function button(wrapper: VueWrapper, text: string) {
  const result = wrapper.findAll("button").find((candidate) => candidate.text() === text);
  if (!result) {
    throw new Error(`Button '${text}' was not rendered.`);
  }
  return result;
}
async function mounted(fetch: ReturnType<typeof vi.fn>) {
  Object.assign(globalThis, { $fetch: fetch });
  const wrapper = mount(Page, {
    global: {
      stubs: {
        PredicateEditor: defineComponent({ props: ["modelValue"], template: "<textarea />" }),
      },
    },
  });
  await flushPromises();
  return wrapper;
}

describe("confirmed graph editing", () => {
  it("does not claim to retain a graph when the initial load fails", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : { ok: false, error: "configuration failed" },
    );
    const wrapper = await mounted(fetch);

    expect(wrapper.text()).toContain("Studio could not load a pipeline.");
    expect(wrapper.text()).not.toContain("Last valid graph retained.");
    wrapper.unmount();
  });
  it("distinguishes inherited, explicitly enabled, and explicitly disabled persistence", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);

    await wrapper.get('[data-testid="select-node-agent"]').trigger("click");
    expect(wrapper.text()).toContain("explicitly disabled for this participant");
    expect(wrapper.text()).toContain("lookup");
    expect(wrapper.text()).toContain('"query"');
    expect(wrapper.text()).toContain("Structured output shape");
    expect(wrapper.text()).toContain('"decision"');
    await wrapper.get('[data-testid="select-node-enabled"]').trigger("click");
    expect(wrapper.text()).toContain("explicitly enabled for this participant");
    await wrapper.get('[data-testid="select-node-done"]').trigger("click");
    expect(wrapper.text()).toContain("inherited from pipeline: enabled");
    wrapper.unmount();
  });

  it("opens direct parallel branch declarations and callbacks", async () => {
    const parallel = response();
    parallel.graph.nodes.push({
      id: "parallel",
      kind: "parallel",
      branches: [
        {
          id: "draft",
          participant: { id: "branch-stage", kind: "stage", persist: false },
        },
      ],
    });
    parallel.ownership.participants["parallel"] = { editable: true };
    parallel.ownership.participants["branch-stage"] = {
      editable: true,
      file: "/project/branches.ts",
      line: 4,
      callbacks: { execute: { file: "/project/branches.ts", line: 5 } },
    };
    const fetch = vi.fn(async (url: string) => {
      if (url === "/api/changes") {
        return { generation: 0 };
      }
      if (url === "/api/open") {
        return { ok: true };
      }
      return parallel;
    });
    const wrapper = await mounted(fetch);

    await wrapper.get('[data-testid="select-node-parallel"]').trigger("click");
    expect(wrapper.text()).toContain("draft → branch-stage (Stage)");
    expect(wrapper.text()).toContain("execute — /project/branches.ts:5");
    await button(wrapper, "Open declaration").trigger("click");
    await button(wrapper, "Open callback").trigger("click");
    expect(fetch).toHaveBeenCalledWith("/api/open", {
      method: "POST",
      body: { kind: "participant", id: "branch-stage" },
    });
    expect(fetch).toHaveBeenCalledWith("/api/open", {
      method: "POST",
      body: { kind: "callback", id: "branch-stage", name: "execute" },
    });
    wrapper.unmount();
  });

  it("renders visible semantic node kinds and output consequences", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);

    expect(wrapper.get('[data-testid="rendered-node-agent"]').text()).toContain("Agent");
    expect(wrapper.get('[data-testid="rendered-node-agent"]').text()).toContain(
      "Execution begins here",
    );
    expect(wrapper.get('[data-testid="rendered-node-done"]').text()).toContain("Successful output");
    expect(wrapper.get('[data-testid="rendered-node-enabled"]').text()).toContain("Stage");
    expect(wrapper.get(".semantic-agent").exists()).toBe(true);
    expect(wrapper.get(".semantic-completion").exists()).toBe(true);
    wrapper.unmount();
  });

  it("qualifies best-effort lifecycle projection only when success is unreachable", async () => {
    let current = response();
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : current,
    );
    const wrapper = await mounted(fetch);
    expect(wrapper.text()).not.toContain("best-effort progression only");

    current = response([{ ...route, target: "enabled" }]);
    await button(wrapper, "Reload").trigger("click");
    await flushPromises();
    expect(wrapper.text()).toContain(
      "No successful completion is reachable without a failure path. The highlighted journey is best-effort progression only.",
    );

    await button(wrapper, "All routes").trigger("click");
    await flushPromises();
    expect(wrapper.text()).not.toContain("best-effort progression only");
    wrapper.unmount();
  });

  it("selects a real route from its Vue Flow source handle by pointer or keyboard", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);
    const handle = wrapper.get('[data-handle-id="port:agent:success:0"]');
    expect(handle.attributes("role")).toBe("button");
    expect(handle.attributes("tabindex")).toBe("0");
    await handle.trigger("click");
    expect(wrapper.text()).toContain("Edit route");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("route:0");

    await wrapper.get('[data-testid="select-node-enabled"]').trigger("click");
    await handle.trigger("keydown", { key: "Enter" });
    expect(wrapper.text()).toContain("Edit route");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("route:0");
    wrapper.unmount();
  });

  it("shows complete selected-route facts for editable and read-only conditional routes", async () => {
    const conditionalRoutes = [
      { ...route, conditional: true },
      {
        id: "route:1",
        source: "agent",
        target: "enabled",
        label: "review elsewhere",
        order: 1,
        outcome: "success" as const,
        conditional: true,
      },
    ];
    const conditional = response(conditionalRoutes);
    Object.assign(conditional.ownership.routes[0]!, {
      file: "/project/pipeline.ts",
      line: 14,
      predicate: "(state) => state.ready === true",
    });
    Object.assign(conditional.ownership.routes[1]!, {
      editable: false,
      file: "/project/pipeline.ts",
      line: 21,
      predicate: "(state) => state.review === 'elsewhere'",
      reason: "Helper-generated conditional route.",
    });
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : conditional,
    );
    const wrapper = await mounted(fetch);

    await wrapper.get('[data-testid="select-route"]').trigger("click");
    expect(wrapper.text()).toContain("Source: agent · Target: done");
    expect(wrapper.text()).toContain("Grouped priority: 1 · Outcome: success · Conditional: yes");
    expect(wrapper.text()).toContain("(state) => state.ready === true");
    expect(wrapper.text()).toContain("Editability: editable");
    expect(wrapper.text()).toContain("Full source location: /project/pipeline.ts:14");
    expect(wrapper.text()).toContain("Validate and save code");

    const readonlyRow = wrapper
      .findAll(".route-row")
      .find((candidate) => candidate.text().includes("Review elsewhere"))!;
    await readonlyRow.trigger("click");
    expect(wrapper.text()).toContain("After agent, continue to enabled");
    expect(wrapper.text()).toContain("Priority 2 for this participant and outcome");
    expect(wrapper.text()).toContain("Participant outcome: success");
    expect(wrapper.text()).toContain("(state) => state.review === 'elsewhere'");
    expect(wrapper.text()).toContain(
      "Editability: read-only — Helper-generated conditional route.",
    );
    expect(wrapper.text()).toContain("Full source location: /project/pipeline.ts:21");
    expect(wrapper.text()).toContain("Read-only: Helper-generated conditional route.");
    wrapper.unmount();
  });

  it("anchors a focused edge label to the midpoint of its bent ELK route", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);

    await wrapper.get('[data-testid="select-route"]').trigger("click");
    const classifiedPath = wrapper.get('[data-testid="rendered-edge"] .vue-flow__edge-path');
    expect(classifiedPath.classes()).toContain("route-terminal");
    const label = wrapper.get(".focused-edge-label");
    expect(label.attributes("style")).toContain("translate(50px,50px)");
    expect(label.attributes("style")).not.toContain("translate(0px,0px)");
    wrapper.unmount();
  });

  it("offers the known source location for a read-only helper route", async () => {
    const readonly = response();
    Object.assign(readonly.ownership.routes[0]!, {
      editable: false,
      file: "/project/pipeline.ts",
      line: 12,
      reason: "Helper-generated route; open the routes array instead.",
    });
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : readonly,
    );
    const wrapper = await mounted(fetch);

    await wrapper.get('[data-testid="select-route"]').trigger("click");
    expect(wrapper.text()).toContain("Read-only: Helper-generated route");
    expect(wrapper.text()).toContain("Open route source");
    expect(wrapper.text()).toContain("/project/pipeline.ts:12");
    wrapper.unmount();
  });

  it("drops absolute ELK edge geometry after drag and refreshes connected edges with manual positions", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);

    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-path")).toContain("M 10 10");
    await wrapper.get('[data-testid="drag-node"]').trigger("click");
    await nextTick();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-path")).toBe("");

    await button(wrapper, "Reload").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-path")).toBe("");
    wrapper.unmount();
  });

  it("temporarily emphasizes hovered routes and restores the existing selection on leave", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);

    await wrapper.get('[data-testid="select-node-enabled"]').trigger("click");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-unrelated")).toBe("true");
    await wrapper.get('[data-testid="hover-route"]').trigger("mouseenter");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-focused")).toBe("true");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-unrelated")).toBe("false");
    await wrapper.get('[data-testid="hover-route"]').trigger("mouseleave");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-focused")).toBe("false");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-unrelated")).toBe("true");
    wrapper.unmount();
  });

  it("reveals a compacted real failure edge on hover or selection and compacts it again afterward", async () => {
    const failureRoutes = [
      route,
      {
        id: "route:1",
        source: "agent",
        target: "failed",
        label: "failure one",
        order: 1,
        outcome: "failed" as const,
        conditional: false,
      },
      {
        id: "route:2",
        source: "enabled",
        target: "failed",
        label: "failure two",
        order: 2,
        conditional: false,
      },
    ];
    const compacted = response(failureRoutes);
    compacted.graph.nodes.push({ id: "failed", kind: "failure" });
    compacted.graph.outputs.push("failed");
    compacted.ownership.participants.failed = { editable: true };
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : compacted,
    );
    const wrapper = await mounted(fetch);

    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");
    const incomingHandle = wrapper.get('[data-handle-id="incoming:failed"]');
    expect(incomingHandle.attributes("style")).toContain("left: -4px");
    expect(incomingHandle.attributes("style")).toContain("top: 27px");
    expect(incomingHandle.attributes("style")).toContain("width: 8px");
    expect(incomingHandle.attributes("style")).toContain("height: 8px");
    const eastHandle = wrapper.get('[data-handle-id="port:agent:success:0"]');
    expect(eastHandle.classes()).toContain("boundary-right-handle");
    expect(eastHandle.attributes("style")).toContain("top: 105px");
    expect(eastHandle.attributes("style")).toContain("right: -4px");
    const failureHandle = wrapper.get('[data-handle-id="port:agent:failed:1"]');
    expect(failureHandle.classes()).toContain("boundary-right-handle");
    expect(failureHandle.element.parentElement?.classList.contains("semantic-card")).toBe(true);
    expect(failureHandle.element.parentElement?.classList.contains("route-row")).toBe(false);
    expect(failureHandle.attributes("style")).toContain("top: 153px");
    expect(failureHandle.attributes("style")).toContain("right: -4px");
    const row = wrapper
      .findAll(".route-row")
      .find((candidate) => candidate.text().includes("Failure one"))!;
    await row.trigger("mouseenter");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("2");
    await row.trigger("mouseleave");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");

    await button(wrapper, "All routes").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("3");
    const allRoutesRow = wrapper
      .findAll(".route-row")
      .find((candidate) => candidate.text().includes("Failure one"))!;
    await allRoutesRow.trigger("click");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("route:1");
    expect(wrapper.text()).toContain("Edit route");

    await button(wrapper, "Lifecycle").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("2");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("route:1");
    expect(wrapper.text()).toContain("Failure one");
    expect(wrapper.text()).toContain("Edit route");
    await wrapper.get('[data-testid="select-node-enabled"]').trigger("click");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");
    wrapper.unmount();
  });

  it("keeps route selection on authored semantics across ordinal shifts without transferring it", async () => {
    const alternate = {
      id: "route:0",
      source: "enabled",
      target: "done",
      label: "alternate",
      order: 0,
      conditional: false,
    };
    const finish = { ...route, id: "route:1", order: 1 };
    let current = response([alternate, finish]);
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : current,
    );
    const wrapper = await mounted(fetch);
    const finishRow = wrapper
      .findAll(".route-row")
      .find((candidate) => candidate.text().includes("Finish"))!;
    await finishRow.trigger("click");
    expect(wrapper.text()).toContain("Edit route");

    const inserted = {
      id: "route:0",
      source: "agent",
      target: "enabled",
      label: "new earlier route",
      order: 0,
      outcome: "success",
      conditional: false,
    };
    current = response([
      inserted,
      { ...alternate, id: "route:1", order: 1 },
      { ...finish, id: "route:2", order: 2 },
    ]);
    await button(wrapper, "Reload").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("route:2");
    expect(wrapper.text()).toContain("Source: agent · Target: done");

    current = response([{ ...finish, id: "route:0", order: 0 }]);
    await button(wrapper, "Reload").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("route:0");
    expect(wrapper.text()).toContain("Edit route");

    current = response([
      { ...finish, id: "route:0", order: 0 },
      { ...alternate, id: "route:1", order: 1 },
    ]);
    await button(wrapper, "Reload").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("route:0");
    expect(wrapper.text()).not.toContain("New earlier route");

    current = response([{ ...alternate, id: "route:0", order: 0 }]);
    await button(wrapper, "Reload").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-focused-edge")).toBe("");
    expect(wrapper.text()).not.toContain("Edit route");
    wrapper.unmount();
  });

  it("retains the last valid graph and diagnoses reload and re-layout ELK failures", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");

    layoutFailure.value = true;
    await button(wrapper, "Reload").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");
    expect(wrapper.text()).toContain("Last valid graph retained.");
    expect(wrapper.text()).toContain("Automatic layout failed: ELK unavailable");

    await button(wrapper, "Re-layout").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");
    expect(wrapper.text()).toContain("Automatic layout failed: ELK unavailable");
    wrapper.unmount();
  });

  it("rolls back a failed mode transition without corrupting the other mode's positions", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);
    await wrapper.get('[data-testid="drag-node"]').trigger("click");
    await nextTick();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-first-node-position")).toBe(
      "37,41",
    );

    layoutFailure.value = true;
    await button(wrapper, "All routes").trigger("click");
    await flushPromises();
    expect(button(wrapper, "Lifecycle").attributes("aria-pressed")).toBe("true");
    expect(button(wrapper, "All routes").attributes("aria-pressed")).toBe("false");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-first-node-position")).toBe(
      "37,41",
    );
    expect(wrapper.text()).toContain("Automatic layout failed: ELK unavailable");

    layoutFailure.value = false;
    await button(wrapper, "All routes").trigger("click");
    await flushPromises();
    expect(button(wrapper, "All routes").attributes("aria-pressed")).toBe("true");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-first-node-position")).toBe("0,0");
    wrapper.unmount();
  });

  it("disables Vue Flow keyboard deletion so confirmed edges cannot disappear in the browser", async () => {
    const fetch = vi.fn(async (url: string) =>
      url === "/api/changes" ? { generation: 0 } : response(),
    );
    const wrapper = await mounted(fetch);

    expect(wrapper.get('[data-testid="flow"]').attributes("data-delete-key")).toBe("null");
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Delete" }));
    await nextTick();

    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");
    expect(fetch).not.toHaveBeenCalledWith("/api/routes", expect.anything());
    wrapper.unmount();
  });

  it("publishes deletion only after successful reconstruction and retains the graph on failure", async () => {
    let saveSucceeds = false;
    const fetch = vi.fn(async (url: string) => {
      if (url === "/api/changes") {
        return { generation: 0 };
      }
      if (url === "/api/routes") {
        return saveSucceeds ? { ok: true } : { ok: false, error: "reconstruction failed" };
      }
      return saveSucceeds ? response([]) : response();
    });
    const wrapper = await mounted(fetch);
    await wrapper.get('[data-testid="select-route"]').trigger("click");
    await button(wrapper, "Delete").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("1");
    expect(wrapper.text()).toContain("reconstruction failed");

    saveSucceeds = true;
    await wrapper.get('[data-testid="select-route"]').trigger("click");
    await button(wrapper, "Delete").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-count")).toBe("0");
    wrapper.unmount();
  });

  it("publishes reconnection only after validation and progressively exposes route decisions", async () => {
    let saveSucceeds = false;
    const reconnected = { ...route, target: "agent" };
    const fetch = vi.fn(async (url: string) => {
      if (url === "/api/changes") {
        return { generation: 0 };
      }
      if (url === "/api/routes") {
        return saveSucceeds ? { ok: true } : { ok: false, error: "reconnect rejected" };
      }
      return saveSucceeds ? response([reconnected]) : response();
    });
    const wrapper = await mounted(fetch);

    await wrapper.get('[data-testid="reconnect"]').trigger("click");
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-target")).toBe("done");
    expect(wrapper.text()).toContain("What happened?");
    expect(wrapper.text()).toContain("Outgoing route priority");
    await button(wrapper, "Validate and save code").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-target")).toBe("done");
    expect(wrapper.text()).toContain("reconnect rejected");

    saveSucceeds = true;
    await button(wrapper, "Validate and save code").trigger("click");
    await flushPromises();
    expect(wrapper.get('[data-testid="flow"]').attributes("data-edge-target")).toBe("agent");

    const savedCalls = fetch.mock.calls.filter(([url]) => url === "/api/routes").length;
    await wrapper.get('[data-testid="connect"]').trigger("click");
    expect(wrapper.text()).toContain("What happened?");
    expect(wrapper.text()).toContain("Route order");
    expect(wrapper.text()).toContain("Optional state condition");
    expect(fetch.mock.calls.filter(([url]) => url === "/api/routes")).toHaveLength(savedCalls);

    await wrapper.get('[data-testid="connect-after-route"]').trigger("click");
    const routeOrder = wrapper
      .findAll("label")
      .find((candidate) => candidate.text().includes("Route order"))!
      .get("select");
    expect((routeOrder.element as HTMLSelectElement).value).toBe("1");
    wrapper.unmount();
  });
});
