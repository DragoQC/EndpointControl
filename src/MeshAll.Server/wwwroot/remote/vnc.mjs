import { socketUrl } from './assets.mjs';

export async function connectVnc(session, options) {
    const { default: RFB } = await import('/vendor/novnc/core/rfb.js');
    if (!session.isActive()) return;
    const credentials = {};
    if (options.username) credentials.username = options.username;
    if (options.password) credentials.password = options.password;
    const client = new RFB(
        session.root.querySelector('#remote-vnc'),
        socketUrl(`/api/network/${options.id}/vnc?port=${options.port}&direct=${options.direct ? 1 : 0}`),
        { credentials }
    );
    session.addCleanup(() => client.disconnect());
    session.sendCredentials = (username, password) => client.sendCredentials({ username, password });
    client.scaleViewport = true;
    client.resizeSession = true;
    client.addEventListener('connect', () => session.report('Connected', true));
    client.addEventListener('disconnect', (event) =>
        session.end(
            event.detail.clean
                ? 'Disconnected'
                : 'VNC service unavailable or authentication failed. Check the connection settings.'
        )
    );
    client.addEventListener('credentialsrequired', (event) => {
        const types = event.detail.types || ['password'];
        session.report('The VNC service requested authentication.', true);
        session.notify('VncCredentialsAsync', types.includes('username'), types.includes('password'));
    });
}
