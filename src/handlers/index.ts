import type { AppBackend } from '../backend/index.js';
import type { Product } from '../lib/product.js';
import type { BaseHandler } from './BaseHandler.js';
import { DocsHandlers } from './DocsHandlers.js';
import { ModsHandlers } from './ModsHandlers.js';
import { NexusHandlers } from './NexusHandlers.js';
import { ProjectsHandlers } from './ProjectsHandlers.js';
import { RecordsHandlers } from './RecordsHandlers.js';
import { SkillsHandlers } from './SkillsHandlers.js';

export { BaseHandler } from './BaseHandler.js';
export type { ToolSpec } from './BaseHandler.js';
export { ToolError, ToolErrorCode } from './errors.js';
export { DocsHandlers, ModsHandlers, NexusHandlers, ProjectsHandlers, RecordsHandlers, SkillsHandlers };

/**
 * Every handler, in tools/list order — the one list.
 *
 * Every product serves exactly these, the tool reference documents exactly these
 * (lib/toolDocs.ts), and the contract tests hold exactly these to the contract.
 * A handler that is not here is not served, not documented and not tested, so
 * forgetting it cannot leave the three disagreeing. `product` is the program
 * serving them: its guides, and the scheme of its resource URIs.
 */
export function createHandlers(backend: AppBackend, product: Product): BaseHandler[] {
    return [
        new DocsHandlers(backend, product),
        new SkillsHandlers(backend, product),
        new RecordsHandlers(backend),
        new ProjectsHandlers(backend),
        new NexusHandlers(backend),
        new ModsHandlers(backend)
    ];
}
