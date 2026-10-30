import fs from 'node:fs';
import path from 'node:path';
import { parseCrashLog } from '../backend/skyrim/crash/crashLog';
import { CrashView } from '../backend/skyrim/crash/crashRules';
import { rankFindings } from '../backend/skyrim/crash/crashReport';
import { CRASH_RULES } from '../backend/skyrim/crash/rules/index';

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
