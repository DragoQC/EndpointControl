// Bundle declared third-party rendering libraries without modifying their code.
// MeshAll's control protocol, agent and browser adapters are separate source.
import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const root = fileURLToPath(new URL('../', import.meta.url));
await mkdir(path.join(root, 'wwwroot/vendor/xterm'), { recursive: true });
for (const file of ['lib/xterm.js', 'css/xterm.css', 'LICENSE'])
    await writeFile(
        path.join(root, 'wwwroot/vendor/xterm', path.basename(file)),
        await readFile(path.join(root, 'node_modules/@xterm/xterm', file))
    );
await cp(path.join(root, 'node_modules/@novnc/novnc'), path.join(root, 'wwwroot/vendor/novnc'), {
    recursive: true
});
await cp(path.join(root, 'node_modules/guacamole-common-js'), path.join(root, 'wwwroot/vendor/guacamole'), {
    recursive: true
});
await cp(path.resolve(root, '../../components/viewers'), path.join(root, 'wwwroot/vendor/native-viewer'), {
    recursive: true
});
console.log('Bundled xterm.js, noVNC and Guacamole rendering libraries with their original licenses.');
