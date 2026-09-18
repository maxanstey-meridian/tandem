import { access } from "node:fs/promises";
import { dirname, isAbsolute, resolve } from "node:path";

export async function discoverConfig(start: string, explicit?: string): Promise<string> {
  if (explicit) {
    const candidate = isAbsolute(explicit) ? explicit : resolve(start, explicit);
    await requireFile(candidate);
    return candidate;
  }
  let directory = resolve(start);
  for (;;) {
    const candidate = resolve(directory, "tandem.config.ts");
    try {
      await access(candidate);
      return candidate;
    } catch {}
    const parent = dirname(directory);
    if (parent === directory) {
      throw new Error(`No tandem.config.ts found from '${start}'.`);
    }
    directory = parent;
  }
}
async function requireFile(path: string): Promise<void> {
  try {
    await access(path);
  } catch {
    throw new Error(`Tandem config '${path}' does not exist.`);
  }
}
