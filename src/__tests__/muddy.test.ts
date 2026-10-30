import fs from 'fs';
import os from 'os';
import path from 'path';
import { CORE_ROOT } from '../lib/product';
import { MUDDY, MUDDY_DIR, MUDDY_WORKSPACE_FILES, muddyPrompt, writeMuddyWorkspace } from '../lib/muddy';

/** Muddy's files, read from the real agents/muddy folder: what ships is what the products install. */

describe('Muddy', () => {
    it('ships every workspace file, in a folder the package publishes', () => {
        for (const file of MUDDY_WORKSPACE_FILES) expect(`${file}: ${fs.existsSync(path.join(MUDDY_DIR, file))}`).toBe(`${file}: true`);
        const files: string[] = JSON.parse(fs.readFileSync(path.join(CORE_ROOT, 'package.json'), 'utf8')).files;
        expect(files).toContain('agents');
    });

    it('names himself and his avatar in IDENTITY.md as MUDDY does', () => {
        const identity = fs.readFileSync(path.join(MUDDY_DIR, 'IDENTITY.md'), 'utf8');
        expect(identity).toContain(`**Name:** ${MUDDY.name}`);
        expect(identity).toContain(`**Avatar:** ${MUDDY.avatar}`);
        expect(identity).toContain(MUDDY.emoji);
    });

    it('gives one prompt of his soul and his working rules, without the files\' titles', () => {
        const prompt = muddyPrompt();
        expect(prompt).toMatch(/^You are Muddy/);
        expect(prompt).toContain('## How you talk');
        expect(prompt).toContain('## How you work');
        expect(prompt).toContain('buildProject');
        expect(prompt).not.toMatch(/^# /m);
    });

    describe('writeMuddyWorkspace', () => {
        let folder: string;
        beforeEach(() => { folder = fs.mkdtempSync(path.join(os.tmpdir(), 'muddy-')); });
        afterEach(() => fs.rmSync(folder, { recursive: true, force: true }));

        it('writes every file into a new folder', () => {
            const target = path.join(folder, 'workspace-muddy');
            expect(writeMuddyWorkspace(target)).toEqual({ written: [...MUDDY_WORKSPACE_FILES], kept: [] });
            expect(fs.readFileSync(path.join(target, 'SOUL.md'), 'utf8')).toBe(fs.readFileSync(path.join(MUDDY_DIR, 'SOUL.md'), 'utf8'));
            expect(fs.statSync(path.join(target, MUDDY.avatar)).size).toBeGreaterThan(1000);
        });

        it('keeps a file the user has, unless told to replace it', () => {
            fs.writeFileSync(path.join(folder, 'SOUL.md'), 'my own crab');
            expect(writeMuddyWorkspace(folder).kept).toEqual(['SOUL.md']);
            expect(fs.readFileSync(path.join(folder, 'SOUL.md'), 'utf8')).toBe('my own crab');
            expect(writeMuddyWorkspace(folder, { overwrite: true }).written).toContain('SOUL.md');
            expect(fs.readFileSync(path.join(folder, 'SOUL.md'), 'utf8')).toMatch(/^# SOUL\.md - Muddy/);
        });
    });
});
