import { RemoteSession } from './session.mjs';
import { RemoteWorkspace } from './workspace.mjs';
import { connectNativeDesktop } from './native-desktop.mjs';
import { connectManagedDesktop } from './managed-desktop.mjs';
import { connectTerminal } from './terminal.mjs';
import { connectVnc } from './vnc.mjs';
import { connectGuacamole } from './guacamole.mjs';

const workspaces = new Map();
let active = null;

function ownedSession(owner) {
    return active?.owner === owner && active.isActive() ? active : null;
}

function disconnect(owner) {
    if (owner && active?.owner !== owner) return;
    const session = active;
    active = null;
    session?.dispose();
}

function showSurface(root, tool) {
    root.querySelector('#remote-canvas').style.display = tool === 'desktop' ? 'block' : 'none';
    root.querySelector('#remote-terminal').style.display = ['terminal', 'powershell'].includes(tool)
        ? 'block'
        : 'none';
    root.querySelector('#remote-vnc').style.display = ['rdp', 'vnc', 'ssh', 'amt'].includes(tool)
        ? 'flex'
        : 'none';
}

function transport(options) {
    switch (options.tool) {
        case 'desktop':
            return options.native ? connectNativeDesktop : connectManagedDesktop;
        case 'terminal':
        case 'powershell':
            return connectTerminal;
        case 'vnc':
            return connectVnc;
        case 'rdp':
        case 'ssh':
            return connectGuacamole;
        default:
            throw Error('This remote transport is still being implemented.');
    }
}

export const remote = {
    mount(owner, root) {
        if (!workspaces.has(owner)) workspaces.set(owner, new RemoteWorkspace(root));
    },
    async dispose(owner) {
        disconnect(owner);
        const workspace = workspaces.get(owner);
        workspaces.delete(owner);
        await workspace?.dispose();
    },
    async connect(options, dotnet) {
        const workspace = workspaces.get(options.owner);
        if (!workspace) return;
        disconnect();
        const session = new RemoteSession(
            options.owner,
            workspace.root,
            dotnet,
            () => active === session,
            !!options.controlRequested && !!options.controlAllowed
        );
        active = session;
        try {
            showSurface(workspace.root, options.tool);
            await transport(options)(session, options);
        } catch (error) {
            if (!session.isActive()) return;
            session.end(error.message || 'Remote connection failed.');
        }
    },
    disconnect,
    control(owner, enabled) {
        const session = ownedSession(owner);
        if (!session) return;
        session.control = enabled;
        session.controlChanged?.(enabled);
    },
    credentials(owner, username, password) {
        ownedSession(owner)?.sendCredentials?.(username, password);
    },
    cad(owner) {
        ownedSession(owner)?.sendCtrlAltDelete?.();
    }
};
