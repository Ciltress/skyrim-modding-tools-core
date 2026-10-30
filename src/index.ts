/**
 * What a product — the MCP server, the OpenClaw plugin — builds from: the backend and its configuration, the
 * one list of tools, the guides and skills they serve, and the rules every answer keeps (profiles, the response
 * budget, the capability gate). A module not exported here is the core's own business.
 */

export * from './backend/index.js';
export * from './backend/skyrim/externalTools.js';
export * from './backend/skyrim/helperClient.js';
export * from './backend/skyrim/mo2.js';
export * from './backend/skyrim/mo2Instances.js';
export * from './backend/skyrim/modManager.js';
export * from './backend/skyrim/paths.js';
export * from './backend/skyrim/pathSettings.js';
export * from './backend/skyrim/platform.js';
export * from './handlers/index.js';
// The repair BaseHandler makes on every call, for a product whose client checks arguments before they reach it.
export { coerceArgs } from './handlers/args.js';
export * from './lib/capabilityGate.js';
export * from './lib/env.js';
export * from './lib/guides.js';
export * from './lib/instructions.js';
export * from './lib/logger.js';
export * from './lib/muddy.js';
export * from './lib/product.js';
export * from './lib/profiles.js';
export * from './lib/responseBudget.js';
export * from './lib/skills.js';
export * from './lib/toolDocs.js';
export type * from './types/tools.js';
