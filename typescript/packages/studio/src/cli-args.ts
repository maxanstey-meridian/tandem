export function parseStudioArguments(args: readonly string[]): { readonly config?: string } {
  if (args.length === 0) {
    return {};
  }
  if (args.length !== 2 || args[0] !== "--config") {
    throw new Error("Usage: tandem-studio [--config <path>]");
  }
  if (!args[1] || args[1].startsWith("-")) {
    throw new Error("--config requires a path.");
  }
  return { config: args[1] };
}
