<script setup lang="ts">
import { Handle, Position } from "@vue-flow/core";
const props = defineProps<{ data: any }>();
function handleSize() {
  return { width: `${props.data.portSize}px`, height: `${props.data.portSize}px` };
}
function portStyle(port: any) {
  return {
    ...handleSize(),
    top: `${port.centerY}px`,
    right: "-4px",
  };
}
function cue(kind: string) {
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
</script>
<template>
  <article
    class="semantic-card"
    :class="`semantic-${data.kind}`"
    :style="{ width: `${data.width}px`, height: `${data.height}px` }"
  >
    <Handle
      :id="data.incomingPort"
      type="target"
      :position="Position.Left"
      :style="{
        ...handleSize(),
        left: `${data.incomingCenterX}px`,
        top: `${data.incomingCenterY}px`,
      }"
    />
    <header>
      <strong>{{ data.title }}</strong
      ><small>{{ cue(data.kind) }}</small>
    </header>
    <small class="authored-id">{{ data.id }}</small>
    <span class="start">{{ data.start ? "Execution begins here" : "" }}</span>
    <div v-for="(ports, outcome) in data.portGroups" :key="String(outcome)" class="outcome-group">
      <small v-if="outcome !== 'default'" class="outcome-heading">{{
        outcome === "success" ? "Succeeded" : "Failed"
      }}</small>
      <div
        v-for="port in ports"
        :key="port.id"
        class="route-row"
        :class="[`route-${port.classification}`, { compacted: port.compacted }]"
        role="button"
        tabindex="0"
        @mouseenter="data.hoverRoute(port.routeId)"
        @mouseleave="data.restoreEmphasis()"
        @click.stop="data.selectRoute(port.routeId)"
        @keydown.enter.stop="data.selectRoute(port.routeId)"
      >
        <span v-if="port.conditional" class="conditional" title="Conditional route">◇</span>
        <span v-if="port.classification === 'correction'" aria-label="Correction route">↩</span>
        <span v-if="port.classification === 'failure'" aria-label="Failure route">!</span>
        <span>{{ port.label }}</span>
      </div>
    </div>
    <Handle
      v-for="port in data.ports"
      :id="port.id"
      :key="port.id"
      class="boundary-route-handle boundary-right-handle"
      type="source"
      :position="Position.Right"
      :style="portStyle(port)"
      role="button"
      tabindex="0"
      :aria-label="`Select route ${port.label}`"
      @click.stop="data.selectRoute(port.routeId)"
      @keydown.enter.stop.prevent="data.selectRoute(port.routeId)"
      @keydown.space.stop.prevent="data.selectRoute(port.routeId)"
    />
    <div v-for="port in data.creationPorts" :key="port.id" class="creation-row">
      <small>Add {{ port.outcome ?? "route" }}</small
      ><Handle :id="port.id" type="source" :position="Position.Right" />
    </div>
  </article>
</template>
