import { fitCanvas, loadScript, socketUrl } from './assets.mjs';

export async function connectNativeDesktop(session, options) {
    for (const file of ['common-0.0.1.js', 'agent-desktop-0.0.2.js', 'agent-redir-ws-0.1.1.js'])
        await loadScript('/vendor/native-viewer/scripts/' + file);
    if (!session.isActive()) return;
    const { DesktopSession, isConsentFailure } = await import('../desktop-session.mjs');
    if (!session.isActive()) return;
    const canvas = session.root.querySelector('#remote-canvas');
    session.observeSize([canvas.parentElement], () => fitCanvas(canvas));
    window.urlargs = {};
    const driver = new DesktopSession({
        controlRequested: options.controlRequested,
        controlAllowed: options.controlAllowed,
        isActive: () => session.isActive(),
        access: () =>
            fetch(`/api/remote/${options.id}/desktop/access`, {
                credentials: 'same-origin',
                cache: 'no-store',
                signal: session.abort.signal
            }),
        report: (text, connected) => session.report(text, connected),
        inputChanged: (allowed, available, enabled) => session.inputState(allowed, available, enabled),
        fit: () => fitCanvas(canvas),
        createDesktop: () => CreateAgentRemoteDesktop(canvas),
        createRedirect: (desktop) => {
            const redirect = CreateAgentRedirect(null, desktop, null, null, null, '/');
            redirect.Start = () => {
                redirect.socket = new WebSocket(socketUrl(`/api/remote/${options.id}/desktop`));
                redirect.socket.binaryType = 'arraybuffer';
                redirect.socket.onopen = redirect.xxOnSocketConnected;
                redirect.socket.onmessage = (event) => {
                    if (typeof event.data === 'string' && event.data.startsWith('{')) {
                        try {
                            const message = JSON.parse(event.data);
                            if (message.type === 'meshall-desktop-ended') {
                                driver.remoteEnded(message.reason);
                                return;
                            }
                            if (message.type === 'meshall-desktop-changed') {
                                driver.desktopChanged();
                                return;
                            }
                        } catch {}
                    }
                    redirect.xxOnMessage(event);
                };
                redirect.socket.onclose = (event) => {
                    if (isConsentFailure(event.reason)) {
                        driver.remoteEnded(event.reason);
                        return;
                    }
                    redirect.xxOnSocketClosed(event);
                };
                redirect.xxStateChange(1);
            };
            return redirect;
        }
    });
    session.addCleanup(() => driver.stop());
    session.controlChanged = (enabled) => driver.setControl(enabled);
    session.sendCtrlAltDelete = () => {
        if (driver.allowed && driver.connected && driver.requested) driver.desktop?.SendCtrlAltDelMsg();
    };
    await driver.attempt();
}
