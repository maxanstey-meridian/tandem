const packages = {
  "darwin-arm64": "@tandem/runtime-darwin-arm64",
};

const platform = `${process.platform}-${process.arch}`;
const packageName = packages[platform];
if (!packageName) {
  throw new Error(
    `Tandem does not ship a runtime for ${platform}. Supported platforms: ${Object.keys(packages).join(", ")}.`,
  );
}

let runtime;
try {
  runtime = await import(packageName);
} catch (error) {
  throw new Error(
    `Tandem could not load ${packageName}. Ensure optional dependencies were installed for ${platform}.`,
    { cause: error },
  );
}

export const runRegisteredGraphAsync = runtime.runRegisteredGraphAsync;
export const inspectAcceptedAsync = runtime.inspectAcceptedAsync;
