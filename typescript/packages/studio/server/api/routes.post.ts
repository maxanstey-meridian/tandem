import { discoverConfig } from "../../src/discovery";
import { applyRouteEdit } from "../../src/edit";
import { loadPipeline } from "../../src/loader";
import type { RouteEdit } from "../../src/source";
interface RouteEditRequest {
  readonly edit: RouteEdit;
  readonly editRevision: string;
}
export default defineEventHandler(async (event) => {
  const config = await discoverConfig(
    process.env.TANDEM_STUDIO_CWD ?? process.cwd(),
    process.env.TANDEM_STUDIO_CONFIG,
  );
  const current = await loadPipeline(config);
  if (!current.ok) {
    return current;
  }
  const request = await readBody<RouteEditRequest>(event);
  if (!request || typeof request.editRevision !== "string" || !request.edit) {
    return { ok: false, error: "A route edit and displayed graph revision are required." };
  }
  return applyRouteEdit(config, current.graph, request.edit, request.editRevision);
});
