if (process.platform !== "darwin" || process.arch !== "arm64") {
  throw new Error(
    `@maxanstey-meridian/tandem-runtime-darwin-arm64 requires macOS arm64; received ${process.platform}-${process.arch}.`,
  );
}

let bridge;
try {
  bridge = await import("./runtime/Tandem.NodeApiSpike.Bridge.mjs");
} catch (error) {
  throw new Error(
    "Tandem requires the .NET 10 runtime and the packaged osx-arm64 runtime assets.",
    { cause: error },
  );
}

export const runRegisteredGraphAsync = bridge.NodePipelineBridge.runRegisteredGraphAsync;
export const inspectAcceptedAsync = bridge.NodePipelineBridge.inspectAcceptedAsync;
