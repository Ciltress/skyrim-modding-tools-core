// Builds the plugin helper into helper/bin with dotnet publish (needs the .NET 10 SDK). In an installed
// core it then removes the helper's C# project: src/backend/skyrim/helperBuild.ts says why.
// usage: npm run build && npm run build:helper
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const { buildHelper } = await import(new URL('../dist/backend/skyrim/helperBuild.js', import.meta.url).href);

const outcome = await buildHelper(root);
if (outcome.ok) console.log(outcome.message);
else {
    console.error(`build-helper failed: ${outcome.message}`);
    process.exitCode = 1;
}
