import { spawn } from "node:child_process";
import type { SourceOwnership } from "./ownership.js";

export type SourceTarget =
  | { readonly kind: "state" }
  | { readonly kind: "participant"; readonly id: string }
  | { readonly kind: "callback"; readonly id: string; readonly name: string }
  | { readonly kind: "route"; readonly order: number };
export interface SourceLocation {
  readonly file: string;
  readonly line: number;
}

export function parseSourceTarget(value: unknown): SourceTarget {
  if (!value || typeof value !== "object") {
    throw new Error("A source target is required.");
  }
  const target = value as Record<string, unknown>;
  if (target.kind === "state" && Object.keys(target).length === 1) {
    return { kind: "state" };
  }
  if (
    target.kind === "participant" &&
    typeof target.id === "string" &&
    Object.keys(target).length === 2
  ) {
    return { kind: "participant", id: target.id };
  }
  if (
    target.kind === "callback" &&
    typeof target.id === "string" &&
    typeof target.name === "string" &&
    Object.keys(target).length === 3
  ) {
    return { kind: "callback", id: target.id, name: target.name };
  }
  if (
    target.kind === "route" &&
    Number.isInteger(target.order) &&
    Object.keys(target).length === 2
  ) {
    return { kind: "route", order: Number(target.order) };
  }
  throw new Error("Invalid source target.");
}

export function resolveSourceTarget(
  ownership: SourceOwnership,
  target: SourceTarget,
): SourceLocation | undefined {
  if (target.kind === "state") {
    return ownership.state?.file && ownership.state.line
      ? { file: ownership.state.file, line: ownership.state.line }
      : undefined;
  }
  if (target.kind === "participant") {
    const participant = ownership.participants[target.id];
    return participant?.file && participant.line
      ? { file: participant.file, line: participant.line }
      : undefined;
  }
  if (target.kind === "callback") {
    return ownership.participants[target.id]?.callbacks?.[target.name];
  }
  const route = ownership.routes[target.order];
  return route?.file && route.line ? { file: route.file, line: route.line } : undefined;
}

export function openSourceLocation(location: SourceLocation, launch: typeof spawn = spawn): void {
  const configured = process.env.TANDEM_STUDIO_EDITOR;
  if (configured) {
    if (/\s/.test(configured)) {
      throw new Error("TANDEM_STUDIO_EDITOR must be an executable name or path without arguments.");
    }
    launch(configured, [`${location.file}:${location.line}`], {
      detached: true,
      stdio: "ignore",
    }).unref();
    return;
  }
  const command =
    process.platform === "darwin" ? "open" : process.platform === "win32" ? "cmd" : "xdg-open";
  const args = process.platform === "win32" ? ["/c", "start", "", location.file] : [location.file];
  launch(command, args, { detached: true, stdio: "ignore" }).unref();
}
