import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { buildHelper, type CommandRunner, type HelperBuildSystem } from '../backend/skyrim/helperBuild';

/**
 * `npm run build:helper` in the core's repository and in an installed core, with `dotnet` replaced by a
 * runner that lays out what a publish leaves: the helper in helper/bin, and bin/obj beside the C# project.
 * A folder another program holds is one `remove` refuses, as Windows refused VS Code's on 2026-10-27.
 */

const roots: string[] = [];
afterAll(() => { for (const root of roots) fs.rmSync(root, { recursive: true, force: true }); });

function tempDir(): string {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'skyrim-helper-build-'));
    roots.push(dir);
    return dir;
}

function write(file: string, text = ''): void {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
}

/** A core at `root` with the helper's C# project, as git and the package carry it. */
function core(root: string): string {
    write(path.join(root, 'helper', 'SkyrimHelper', 'SkyrimHelper.csproj'), '<Project />');
    write(path.join(root, 'helper', 'SkyrimHelper', 'Program.cs'));
    return root;
}

interface Call { command: string; args: readonly string[]; cwd: string }

function publisher(code = 0, held: string[] = []): HelperBuildSystem & { calls: Call[] } {
    const calls: Call[] = [];
    const run: CommandRunner = async (command, args, cwd) => {
        calls.push({ command, args, cwd });
        if (code === 0) {
            write(path.join(cwd, 'helper', 'bin', 'SkyrimHelper.dll'));
            write(path.join(cwd, 'helper', 'SkyrimHelper', 'bin', 'Release', 'net10.0', 'SkyrimHelper.dll'));
            write(path.join(cwd, 'helper', 'SkyrimHelper', 'obj', 'project.assets.json'));
        }
        return code;
    };
    const remove = (target: string) => {
        if (held.some(name => target.endsWith(name))) throw new Error(`EPERM, Permission denied: '${target}'`);
        fs.rmSync(target, { recursive: true, force: true });
    };
    return { run, remove, calls };
}

const PUBLISH = ['publish', 'helper/SkyrimHelper', '-c', 'Release', '-o', 'helper/bin', '-nologo'];

describe('building the helper in the core\'s repository', () => {
    it('publishes into helper/bin and keeps the C# project', async () => {
        const root = core(tempDir());
        const system = publisher();
        const { calls } = system;

        const outcome = await buildHelper(root, system);

        expect(outcome).toEqual({ ok: true, message: `The plugin helper is built in ${path.join(root, 'helper', 'bin')}.` });
        expect(calls).toEqual([{ command: 'dotnet', args: PUBLISH, cwd: root }]);
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper', 'SkyrimHelper.csproj'))).toBe(true);
    });

    it('treats a repository linked into node_modules as the repository', async () => {
        const repo = core(tempDir());
        const link = path.join(tempDir(), 'node_modules', 'skyrim-modding-tools-core');
        fs.mkdirSync(path.dirname(link), { recursive: true });
        fs.symlinkSync(repo, link, 'junction');
        const system = publisher();
        const { calls } = system;

        expect((await buildHelper(link, system)).ok).toBe(true);
        expect(calls[0].args).toEqual(PUBLISH);
        expect(fs.existsSync(path.join(repo, 'helper', 'SkyrimHelper', 'SkyrimHelper.csproj'))).toBe(true);
    });
});

describe('building the helper in an installed core', () => {
    const installed = () => core(path.join(tempDir(), 'node_modules', 'skyrim-modding-tools-core'));

    it('removes the C# project with its build output once the helper is in helper/bin', async () => {
        const root = installed();
        const system = publisher();

        const outcome = await buildHelper(root, system);

        expect(outcome.ok).toBe(true);
        expect(outcome.message).toMatch(/built in .*helper[\\/]bin; its C# project was removed .* reinstall skyrim-modding-tools-core\.$/);
        expect(system.calls).toEqual([{ command: 'dotnet', args: [...PUBLISH, '--disable-build-servers'], cwd: root }]);
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper'))).toBe(false);
        expect(fs.existsSync(path.join(root, 'helper', 'bin', 'SkyrimHelper.dll'))).toBe(true);
    });

    it('removes the project file first, and only notes the rest of the folder when another program holds it', async () => {
        const root = installed();

        const outcome = await buildHelper(root, publisher(0, ['SkyrimHelper']));

        expect(outcome.ok).toBe(true);
        expect(outcome.message).toMatch(/its C# project was removed .* Another program holds the rest of .*SkyrimHelper \(EPERM.*\), most likely an editor .*: run npm run build:helper again once it lets go\.$/);
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper', 'SkyrimHelper.csproj'))).toBe(false);
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper', 'obj', 'project.assets.json'))).toBe(true);
    });

    it('fails when the project file itself cannot be removed', async () => {
        const root = installed();

        const outcome = await buildHelper(root, publisher(0, ['SkyrimHelper.csproj']));

        expect(outcome.ok).toBe(false);
        expect(outcome.message).toMatch(/built in .*, but its project file .*SkyrimHelper\.csproj could not be removed \(EPERM.*\): close what holds it/);
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper', 'SkyrimHelper.csproj'))).toBe(true);
    });

    it('run again, says the helper is built and clears what a held folder left', async () => {
        const root = installed();
        await buildHelper(root, publisher(0, ['SkyrimHelper']));
        const system = publisher();

        const outcome = await buildHelper(root, system);

        expect(outcome.ok).toBe(true);
        expect(outcome.message).toMatch(/already built in .*: reinstall skyrim-modding-tools-core for that\.$/);
        expect(system.calls).toEqual([]);
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper'))).toBe(false);
    });

    it('without a C# project or a built helper, says to reinstall the core', async () => {
        const root = path.join(tempDir(), 'node_modules', 'skyrim-modding-tools-core');
        fs.mkdirSync(root, { recursive: true });
        const system = publisher();
        const { calls } = system;

        expect(await buildHelper(root, system)).toEqual({
            ok: false,
            message: `No C# project at ${path.join(root, 'helper', 'SkyrimHelper')} to build the plugin helper from: reinstall skyrim-modding-tools-core.`
        });
        expect(calls).toEqual([]);
    });

    it('keeps the C# project when the publish fails', async () => {
        const root = installed();

        expect(await buildHelper(root, publisher(1))).toEqual({ ok: false, message: 'dotnet publish failed with exit code 1: see its output above.' });
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper', 'SkyrimHelper.csproj'))).toBe(true);
    });

    it('names the .NET SDK when dotnet cannot be started', async () => {
        const root = installed();
        const run: CommandRunner = async () => { throw new Error('spawn dotnet ENOENT'); };

        const outcome = await buildHelper(root, { ...publisher(), run });

        expect(outcome.ok).toBe(false);
        expect(outcome.message).toBe('Could not start dotnet (spawn dotnet ENOENT): the plugin helper needs the .NET 10 SDK.');
        expect(fs.existsSync(path.join(root, 'helper', 'SkyrimHelper', 'SkyrimHelper.csproj'))).toBe(true);
    });
});
