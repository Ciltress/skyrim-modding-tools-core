import fs from 'fs';
import path from 'path';
import { CORE_ROOT, resourceScheme, type Product } from './product.js';

/**
 * A product's own documentation, exposed to clients both as MCP resources and
 * through the readServerGuide tool.
 *
 * Two ways out because they reach different consumers: resources are the
 * idiomatic MCP primitive but are typically *user*-attached in a client's UI, so
 * an agent will not discover them on its own. Tools are always in the agent's
 * context, so the tool is what actually lets an agent look something up
 * mid-task. Both read the same files through this module.
 *
 * Which guides there are is the product's: `Product.guides`, the core's from
 * CORE_GUIDES among its own, in the order it lists them.
 */

export interface GuideDefinition {
    /** Stable id used by the tool and in the resource URI. */
    id: string;
    title: string;
    description: string;
    /** The folder `file` is in: CORE_ROOT for the core's guides, the product's root for its own. */
    root: string;
    /** Path relative to `root`. */
    file: string;
}

/**
 * The core's guides, for a product to list among its own. guides.test.ts reads
 * the real files, so a guide moved without updating this fails the suite, not a
 * client.
 */
export const CORE_GUIDES = {
    // Listed first by the products on purpose: it is the cheapest useful thing to
    // read, and the one that answers the question an agent actually has — "which
    // tool does this?"
    router: {
        id: 'router',
        title: 'Which tool does what you want',
        root: CORE_ROOT,
        file: 'docs/Tool-Router.md',
        description:
            'Intent to tool, in the words people use. Hand-written, and the right first stop when you ' +
            'know the job but not the tool name.'
    },
    tools: {
        id: 'tools',
        title: 'How this server behaves',
        root: CORE_ROOT,
        file: 'docs/MCP-Tools.md',
        description:
            'The rules that prevent most failures, the golden-path workflows, the response and error ' +
            'model, profiles and the response budget, and a troubleshooting matrix.'
    }
} as const satisfies Record<string, GuideDefinition>;

/** URI prefix for the resource form of a product's guide. */
export const guideUriPrefix = (product: Product) => `${resourceScheme(product)}://guides/`;

export const guideUri = (product: Product, id: string) => `${guideUriPrefix(product)}${id}`;

export function guideById(product: Product, id: string): GuideDefinition | undefined {
    return product.guides.find(g => g.id === String(id ?? '').trim().toLowerCase());
}

export function guideByUri(product: Product, uri: string): GuideDefinition | undefined {
    const prefix = guideUriPrefix(product);
    const id = String(uri ?? '').startsWith(prefix)
        ? String(uri).slice(prefix.length)
        : undefined;
    return id ? guideById(product, id) : undefined;
}

export class GuideNotFoundError extends Error { }

export function readGuideFile(guide: GuideDefinition): string {
    const full = path.join(guide.root, guide.file);
    try {
        return fs.readFileSync(full, 'utf8');
    } catch {
        throw new GuideNotFoundError(
            `The guide '${guide.id}' is not readable at ${guide.file}. ` +
            `Documentation ships alongside the build; check the installation.`
        );
    }
}

export interface GuideSection {
    /** Heading number when the heading is numbered, e.g. '4.9'. */
    number?: string;
    /** Heading text without the leading #s and number. */
    title: string;
    /** 2 for '##', 3 for '###'. */
    level: number;
    /** Line index of the heading within the document. */
    line: number;
}

/** Markdown headings at level 2 and 3 — the granularity worth addressing. */
export function listSections(markdown: string): GuideSection[] {
    const sections: GuideSection[] = [];
    const lines = markdown.split(/\r?\n/);
    let inFence = false;

    lines.forEach((line, index) => {
        // Headings inside a fenced block are content, e.g. a '# comment' in a snippet.
        if (/^\s*```/.test(line)) inFence = !inFence;
        if (inFence) return;

        const heading = line.match(/^(#{2,3})\s+(.*)$/);
        if (!heading) return;

        const level = heading[1].length;
        const text = heading[2].trim();
        const numbered = text.match(/^(\d+(?:\.\d+)*)\.?\s+(.*)$/);

        sections.push({
            number: numbered?.[1],
            title: numbered ? numbered[2].trim() : text,
            level,
            line: index
        });
    });

    return sections;
}

/**
 * Finds one section by heading number ('4.9'), by a case-insensitive fragment of
 * its title, or by both ('6.2 the tools'). Returns the heading and everything
 * below it up to the next heading of the same or a higher level.
 */
export function extractSection(
    markdown: string,
    query: string
): { section: GuideSection; content: string } | undefined {
    const wanted = String(query ?? '').trim().toLowerCase();
    if (!wanted) return undefined;

    const sections = listSections(markdown);
    const match =
        // A bare number must match exactly, so '4' does not select '4.9'.
        sections.find(s => s.number && s.number === wanted) ??
        sections.find(s => s.title.toLowerCase() === wanted) ??
        sections.find(s => `${s.number ?? ''} ${s.title}`.trim().toLowerCase() === wanted) ??
        sections.find(s => s.title.toLowerCase().includes(wanted)) ??
        sections.find(s => !!s.number && s.number.startsWith(`${wanted}.`));

    if (!match) return undefined;

    const lines = markdown.split(/\r?\n/);
    const start = match.line;
    const next = sections.find(s => s.line > start && s.level <= match.level);
    const end = next ? next.line : lines.length;

    return { section: match, content: lines.slice(start, end).join('\n').trimEnd() };
}

/** One entry of the cheap index returned when no particular guide is asked for. */
export function guideIndex(product: Product) {
    return product.guides.map(guide => {
        let sections: { number?: string; title: string; level: number }[] = [];
        let bytes = 0;
        try {
            const text = readGuideFile(guide);
            bytes = Buffer.byteLength(text, 'utf8');
            sections = listSections(text).map(({ number, title, level }) => ({ number, title, level }));
        } catch {
            // A missing file must not break the whole index.
        }
        return {
            id: guide.id,
            title: guide.title,
            description: guide.description,
            uri: guideUri(product, guide.id),
            bytes,
            sections
        };
    });
}
