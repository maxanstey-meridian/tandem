import { configWorkspace, watchProjectTypescript } from "../../src/watch";

export default defineEventHandler(() => {
  const config = process.env.TANDEM_STUDIO_CONFIG;
  const generation = watchProjectTypescript(
    config ? configWorkspace(config) : (process.env.TANDEM_STUDIO_CWD ?? process.cwd()),
  );
  return { generation: generation() };
});
