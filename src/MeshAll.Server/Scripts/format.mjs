import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// Keep the write/check scope identical and restricted to first-party sources.
const files = [
    'wwwroot/app.js',
    'wwwroot/dialog.mjs',
    'wwwroot/remote/*.mjs',
    'wwwroot/desktop-session.mjs',
    'wwwroot/MeshAll.Server.lib.module.js',
    'Styles/app.css',
    'Scripts/*.mjs',
    '../../components/agent-core/modules_meshcore/meshall-*.js',
    '../../tests/*.cjs',
    '../../tests/*.mjs'
];
const mode = process.argv[2] || '--check';
if (!['--check', '--write'].includes(mode)) throw Error('Use --check or --write.');

const result = spawnSync(
    process.execPath,
    [fileURLToPath(import.meta.resolve('prettier/bin/prettier.cjs')), mode, ...files],
    { cwd: fileURLToPath(new URL('../', import.meta.url)), stdio: 'inherit' }
);
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
