import type { z } from "zod";
export type PacketFile<T> = {
    readonly value: T;
    readonly context: string;
    readonly source: PacketSource;
};
export type PacketSource = {
    readonly name?: string;
    readonly fullPath?: string;
    readonly directory?: string;
    resolvePath(path: string): string;
};
export type PacketProblem = {
    readonly path: string;
    readonly message: string;
    readonly line?: number;
    readonly column?: number;
};
export declare class PacketFileError extends Error {
    readonly sourceName?: string;
    readonly problems: readonly PacketProblem[];
    constructor(message: string, options: {
        readonly sourceName?: string;
        readonly problems: readonly PacketProblem[];
        readonly cause?: unknown;
    });
}
export declare function parsePacketFile<TSchema extends z.ZodType>(content: string, schema: TSchema, options?: {
    readonly sourceName?: string;
}): PacketFile<z.output<TSchema>>;
export declare function readPacketFile<TSchema extends z.ZodType>(path: string | URL, schema: TSchema, options?: {
    readonly signal?: AbortSignal;
}): Promise<PacketFile<z.output<TSchema>>>;
