import fs from 'node:fs';
import path from 'node:path';
import { parseCrashLog } from '../backend/skyrim/crash/crashLog';
import { CrashView } from '../backend/skyrim/crash/crashRules';
import { rankFindings } from '../backend/skyrim/crash/crashReport';
import { CRASH_RULES } from '../backend/skyrim/crash/rules/index';
import { candidates } from '../backend/skyrim/crash/suspects';

/**
 * Every crash rule together, as readCrashLog runs them: one list, ids unique, and on every real log each rule
 * answers — never throws — with findings a model can read: plain text in bounds, evidence from the log.
 */

const FIXTURES = path.join(__dirname, 'fixtures', 'crash-logs');
const LOGS = fs.readdirSync(FIXTURES).filter(name => name.endsWith('.log'));

describe('every crash rule', () => {
    it('has an id of its own, and says what it ports', () => {
        const ids = CRASH_RULES.map(rule => rule.id);
        expect(ids.filter((id, i) => ids.indexOf(id) !== i)).toEqual([]);
        for (const rule of CRASH_RULES) {
            expect(rule.id, rule.id).toMatch(/^[a-z0-9]+(-[a-z0-9]+)*$/);
            expect(rule.ported, rule.id).toMatch(/^(diagnoses\.js \w+|analyzeLog\.js: .+)$/);
        }
        expect(CRASH_RULES.length).toBeGreaterThan(100);
    });

    it.each(LOGS)('reads %s without throwing, each finding plain text with evidence from the log', name => {
        const text = fs.readFileSync(path.join(FIXTURES, name), 'utf8');
        const view = new CrashView(parseCrashLog(text));
        for (const rule of CRASH_RULES) {
            const findings = [rule.diagnose(view) ?? []].flat();
            for (const found of findings) {
                expect(found.title.length, `${rule.id}: title`).toBeLessThanOrEqual(80);
                for (const part of [found.title, found.explain, found.fix]) {
                    expect(part, `${rule.id}: no markup`).not.toMatch(/<\/?[a-z]+[ >]|\*\*|\]\(|https?:\/\//i);
                }
                for (const item of found.evidence) expect(text, `${rule.id}: evidence is the log's`).toContain(item.text);
            }
        }
        expect(() => rankFindings(view, CRASH_RULES)).not.toThrow();
    });

    it('ranks a mesh crash\'s diagnosis first in the 1.27 log that names the model being loaded', () => {
        const view = new CrashView(parseCrashLog(fs.readFileSync(path.join(FIXTURES, 'synthetic-1.27-mesh.log'), 'utf8')));
        expect(rankFindings(view, CRASH_RULES)[0]).toMatchObject({ rule: 'mesh-issue', confidence: 'likely' });
    });
});

/**
 * Crashes made on purpose in the test instance (game 1.7.104, Crash Logger 1.27.0, 2026-10-30), whose cause is known:
 * an SKSE plugin writing through a null pointer once the game's data is loaded (LanternGlow.dll), and a main-menu logo
 * mesh whose root links a shader property as a child (the mod Elder Banner Main Menu).
 */
describe('crashes with a known cause', () => {
    const read = (name: string) => parseCrashLog(fs.readFileSync(path.join(FIXTURES, name), 'utf8'));

    it('puts an SKSE plugin\'s crash on its DLL, first in diagnoses and suspects', () => {
        const log = read('crash-2026-10-30-06-13-38.log');
        expect(rankFindings(new CrashView(log), CRASH_RULES)[0]).toMatchObject({
            rule: 'first-line-module', confidence: 'likely', title: expect.stringContaining('LanternGlow.dll')
        });
        expect(candidates(log)[0]).toMatchObject({ file: 'LanternGlow.dll', kind: 'dll', evidence: [{ place: 'firstLine' }, { place: 'callStack' }] });
        // Crash Logger 1.27 checked the masters itself and found none missing.
        expect(rankFindings(new CrashView(log), CRASH_RULES).map(found => found.rule)).not.toContain('missing-masters');
    });

    it('puts a broken mesh\'s crash on the mesh the crashing thread was handling, one file however it is spelled', () => {
        const log = read('crash-2026-10-30-06-24-26.log');
        const [top] = rankFindings(new CrashView(log), CRASH_RULES);
        expect(top).toMatchObject({ rule: 'mesh-issue', confidence: 'likely', title: 'Crashed while handling the model logo.nif' });
        expect(top.evidence[0].place).toBe('modelPaths');
        const models = candidates(log).filter(candidate => candidate.kind === 'model');
        expect(models.map(model => model.file)).toEqual(['meshes\\interface\\logo\\logo.nif']);
        const places = models[0].evidence.map(item => item.place);
        expect(places[0]).toBe('modelPaths');
        expect(places.slice(1).every(place => place === 'stack')).toBe(true);
    });
});
