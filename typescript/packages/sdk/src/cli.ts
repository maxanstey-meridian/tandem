export function closeCli(exitCode = 0): never {
  process.exit(exitCode);
}
