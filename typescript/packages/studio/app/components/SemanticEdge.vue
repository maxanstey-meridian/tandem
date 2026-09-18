<script setup lang="ts">
import { BaseEdge, EdgeLabelRenderer, getBezierPath, type EdgeProps } from "@vue-flow/core";
const props = defineProps<EdgeProps>();
const path = computed(() => getBezierPath(props));
const labelAnchor = computed(() =>
  props.data?.elkPath && props.data?.elkAnchor
    ? [props.data.elkAnchor.x, props.data.elkAnchor.y]
    : [path.value[1], path.value[2]],
);
</script>
<template>
  <BaseEdge
    :id="id"
    :path="data.elkPath || path[0]"
    :marker-end="markerEnd"
    :class="[`route-${data.classification}`, { unrelated: data.unrelated }]"
  />
  <EdgeLabelRenderer v-if="data.focused">
    <span
      class="focused-edge-label"
      :style="{
        transform: `translate(-50%, -50%) translate(${labelAnchor[0]}px,${labelAnchor[1]}px)`,
      }"
      >{{ data.label }}</span
    >
  </EdgeLabelRenderer>
</template>
