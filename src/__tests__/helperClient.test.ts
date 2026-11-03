import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { HelperClient } from '../backend/skyrim/helperClient.js';

// A stand-in for the .NET helper, speaking its JSON lines: each call is answered after the `delay` it asks for, and
// it ends with its stdin, as the helper does.
const FAKE_HELPER = `
const lines = require('node:readline').createInterface({ input: process.stdin });
lines.on('line', line => {
    const { id, params } = JSON.parse(line);
    setTimeout(() => process.stdout.write(JSON.stringify({ id, result: { echoed: params } }) + '\\n'), params.delay ?? 0);
});
lines.on('close', () => process.exit(0));
`;

/** What a child process brings that keeps this process's event loop alive: the process itself and its pipes. */
function holding(): Record<string, number> {
    const counts: Record<string, number> = { ProcessWrap: 0, PipeWrap: 0 };
    for (const kind of process.getActiveResourcesInfo()) if (kind in counts) counts[kind]++;
    return counts;
}

describe('HelperClient', () => {
    let script: string;
    let client: HelperClient | undefined;

    beforeAll(() => {
        script = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'helper-client-')), 'fake-helper.cjs');
        fs.writeFileSync(script, FAKE_HELPER);
    });
    afterEach(() => {
        vi.restoreAllMocks();
        client?.stop();
        client = undefined;
    });

    // OpenClaw 2026.10.10's `agent exec` ends when nothing keeps its event loop alive, where 2026.10.9 exited on its
    // own: a helper started by a tool call kept the run going until it was killed, 21 minutes later, in every run of
    // the eval after the update (2026-11-03). The helper ends with its host's stdin, so an idle one may hold nothing.
    it('holds nothing that keeps its host process alive while idle', async () => {
        const before = holding();
        client = new HelperClient(process.execPath, [script]);
        await client.start();
        await expect(client.call('ping', { n: 1 })).resolves.toEqual({ echoed: { n: 1 } });
        expect(holding()).toEqual(before);
    });

    it('keeps its host alive by the timer of a call that waits for its answer', async () => {
        client = new HelperClient(process.execPath, [script]);
        await client.start();
        const real = globalThis.setTimeout;
        const timers: NodeJS.Timeout[] = [];
        vi.spyOn(globalThis, 'setTimeout').mockImplementation(((...args: Parameters<typeof setTimeout>) => {
            const timer = real(...args);
            timers.push(timer);
            return timer;
        }) as typeof setTimeout);
        const answer = client.call('slow', { delay: 300 });
        expect(timers.filter(timer => timer.hasRef())).toHaveLength(1);
        await expect(answer).resolves.toEqual({ echoed: { delay: 300 } });
    });
});
