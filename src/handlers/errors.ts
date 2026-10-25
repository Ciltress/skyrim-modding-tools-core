/**
 * How a tool fails: a code and a message that says what to do instead — a model reads the message and acts on it.
 *
 * The codes are JSON-RPC's, the numbers MCP's ErrorCode has, so the MCP server turns a ToolError into an McpError
 * without a table, and the OpenClaw plugin shows the message as it stands.
 */
export const ToolErrorCode = {
    /** The request cannot be served as it stands: no skills installed, a tool this backend lacks. */
    InvalidRequest: -32600,
    /** No tool by that name. */
    MethodNotFound: -32601,
    /** An argument is wrong, missing, or names something that does not exist. */
    InvalidParams: -32602,
    /** Anything else: the server's fault or the machine's, not the request's. */
    InternalError: -32603
} as const;

export type ToolErrorCode = (typeof ToolErrorCode)[keyof typeof ToolErrorCode];

export class ToolError extends Error {
    constructor(readonly code: ToolErrorCode, message: string) {
        super(message);
        this.name = 'ToolError';
    }
}
