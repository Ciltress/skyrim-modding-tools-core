import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

/**
 * What `npm run build:helper` does: `dotnet publish` of the plugin helper (docs/adr/0003) into
 * `helper/bin`. In an installed core — one under `node_modules` — it then removes `helper/SkyrimHelper`,
 * the C# project with the `bin` and `obj` the publish leaves beside it. OpenClaw copies the whole package
 * for every plugin generation, and an editor that finds a C# project in such a copy builds it there and
 * holds the copy open, which OpenClaw then cannot replace (docs/adr/0009). `helper/bin` is all an
 * installed core runs; a new version of the core brings the project back.
 */

/** Runs a command with its output on this process's, and answers its exit code. */
export type CommandRunner = (command: string, args: readonly string[], cwd: string) => Promise<number>;

/** What a build did, as the one line it ends with; `ok` is false when the helper may not be built. */
export interface HelperBuildOutcome {
    readonly ok: boolean;
    readonly message: string;
}

const PUBLISH = ['publish', 'helper/SkyrimHelper', '-c', 'Release', '-o', 'helper/bin', '-nologo'];

export async function buildHelper(root: string, run: CommandRunner = runCommand): Promise<HelperBuildOutcome> {
    const project = path.join(root, 'helper', 'SkyrimHelper');
    const bin = path.join(root, 'helper', 'bin');
    const installed = isInstalled(root);
    if (!fs.existsSync(path.join(project, 'SkyrimHelper.csproj'))) {
        if (installed && fs.existsSync(path.join(bin, 'SkyrimHelper.dll'))) {
            return removeProject(project, bin) ?? {
                ok: true,
                message: `The plugin helper is already built in ${bin}. This installed core keeps no C# project to build it ` +
                    `again: reinstall skyrim-modding-tools-core for that.`
            };
        }
        return { ok: false, message: `No C# project at ${project} to build the plugin helper from${installed ? ': reinstall skyrim-modding-tools-core' : ''}.` };
    }

    let code: number;
    try {
        // No MSBuild node or compiler server may outlive the build: one would hold the folder removed next.
        code = await run('dotnet', installed ? [...PUBLISH, '--disable-build-servers'] : PUBLISH, root);
    } catch (error) {
        return { ok: false, message: `Could not start dotnet (${(error as Error).message}): the plugin helper needs the .NET 10 SDK.` };
    }
    if (code !== 0) return { ok: false, message: `dotnet publish failed with exit code ${code}: see its output above.` };
    if (!installed) return { ok: true, message: `The plugin helper is built in ${bin}.` };
    return removeProject(project, bin) ?? {
        ok: true,
        message: `The plugin helper is built in ${bin}; its C# project was removed from this installed core, so that no copy ` +
            `of it carries one. To build the helper again, reinstall skyrim-modding-tools-core.`
    };
}

/** Whether the core at `root` is a dependency's copy, under `node_modules`, rather than its repository linked there. */
function isInstalled(root: string): boolean {
    return fs.realpathSync(root).split(path.sep).includes('node_modules');
}

/** Removes the C# project; answers the failure when something holds it. */
function removeProject(project: string, bin: string): HelperBuildOutcome | undefined {
    try {
        fs.rmSync(project, { recursive: true, force: true, maxRetries: 3 });
        return undefined;
    } catch (error) {
        return {
            ok: false,
            message: `The plugin helper is built in ${bin}, but its C# project could not be removed from ${project} ` +
                `(${(error as Error).message}): close what holds it, then run npm run build:helper again.`
        };
    }
}

function runCommand(command: string, args: readonly string[], cwd: string): Promise<number> {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { cwd, stdio: 'inherit' });
        child.on('error', reject);
        child.on('exit', code => resolve(code ?? 1));
    });
}
