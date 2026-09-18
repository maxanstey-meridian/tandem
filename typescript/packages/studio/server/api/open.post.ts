import { discoverConfig } from "../../src/discovery";
import { loadPipeline } from "../../src/loader";
import { locateOwnership } from "../../src/ownership";
import { openSourceLocation, parseSourceTarget, resolveSourceTarget } from "../../src/source-open";

export default defineEventHandler(async (event) => {
  const target = parseSourceTarget(await readBody(event));
  const config = await discoverConfig(
    process.env.TANDEM_STUDIO_CWD ?? process.cwd(),
    process.env.TANDEM_STUDIO_CONFIG,
  );
  const loaded = await loadPipeline(config);
  if (!loaded.ok) {
    return loaded;
  }
  const location = resolveSourceTarget(locateOwnership(config, loaded.graph), target);
  if (!location) {
    return { ok: false, error: "This source location is unavailable or ambiguous." };
  }
  openSourceLocation(location);
  return { ok: true };
});
