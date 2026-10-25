import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import type { GuideDefinition } from './guides.js';

/**
 * The core's own folder: where the helper is built (helper/bin), and where the skills and the core's guides are.
 * This file compiles to dist/lib/product.js and runs from src/lib under vitest, both two levels below the root, so
 * the same relative hop works either way.
 */
export const CORE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

const packageJson = JSON.parse(fs.readFileSync(path.join(CORE_ROOT, 'package.json'), 'utf8'));

/** The core's name and version, from its package.json. */
export const CORE_INFO: { name: string; version: string } = {
    name: String(packageJson.name),
    version: String(packageJson.version)
};

/**
 * The program a modder runs, which serves the core's tools: the MCP server, the OpenClaw plugin. What differs
 * between them and the tools still need — whose name a resource URI carries, which version Nexus is told, where
 * the projects are — the product says, so the core never guesses which of them it runs in.
 */
export interface Product {
    /** The product's package name. Its resource URIs' scheme is derived from it (resourceScheme). */
    name: string;
    /** Its version, sent to Nexus Mods with every request. */
    version: string;
    /**
     * Its folder: a relative path setting starts here, and projects/ and tools/ (the Papyrus compiler setup
     * downloads) are beside it unless a setting says otherwise.
     */
    root: string;
    /** Every guide it serves, in the order it lists them: CORE_GUIDES' and its own. */
    guides: readonly GuideDefinition[];
}

/**
 * The URI scheme of a product's resources — `<scheme>://guides/<id>` and `<scheme>://skills/<collection>/<name>`.
 * Derived from its package name, so a renamed product does not keep serving another's URIs. A scoped name loses
 * its `@` and its `/`, which a scheme cannot contain.
 */
export function resourceScheme(product: Pick<Product, 'name'>): string {
    return product.name
        .replace(/^@/, '')
        .replace(/[^a-z0-9+.-]/gi, '-')
        .toLowerCase();
}
