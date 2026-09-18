<script setup lang="ts">
import { javascript } from "@codemirror/lang-javascript";
import { EditorView, basicSetup } from "codemirror";
const value = defineModel<string>({ required: true });
const host = ref<HTMLElement>();
let view: EditorView | undefined;
onMounted(
  () =>
    (view = new EditorView({
      parent: host.value,
      doc: value.value,
      extensions: [
        basicSetup,
        javascript({ typescript: true }),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            value.value = update.state.doc.toString();
          }
        }),
      ],
    })),
);
onBeforeUnmount(() => view?.destroy());
</script>
<template><div ref="host" class="editor" /></template>
<style scoped>
.editor {
  border: 1px solid #ccd3df;
  min-height: 72px;
}
</style>
