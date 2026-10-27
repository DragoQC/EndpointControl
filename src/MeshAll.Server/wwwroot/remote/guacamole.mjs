import { socketUrl } from './assets.mjs';

export async function connectGuacamole(session, options) {
    const { default: Guacamole } = await import('/vendor/guacamole/dist/esm/guacamole-common.js');
    if (!session.isActive()) return;
    const surface = session.root.querySelector('#remote-vnc');
    const tunnel = createTunnel(session, options, Guacamole);
    const client = new Guacamole.Client(tunnel);
    session.addCleanup(() => client.disconnect());
    const display = client.getDisplay();
    const element = display.getElement();
    // Guacamole uses negative layer z-indexes; isolate them above the surface background.
    element.style.isolation = 'isolate';
    surface.replaceChildren(element);
    session.addCleanup(() => element.remove());
    const fit = () => {
        if (display.getWidth() > 0 && display.getHeight() > 0)
            display.scale(
                Math.min(surface.clientWidth / display.getWidth(), surface.clientHeight / display.getHeight())
            );
    };
    display.onresize = fit;
    session.observeSize([surface], fit);
    const mouse = new Guacamole.Mouse(element);
    mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = (state) => client.sendMouseState(state, true);
    const keyboard = new Guacamole.Keyboard(element);
    session.addCleanup(() => {
        keyboard.reset();
        keyboard.onkeydown = keyboard.onkeyup = null;
        mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = null;
    });
    element.tabIndex = 0;
    session.listen(element, 'pointerdown', () => element.focus());
    keyboard.onkeydown = (key) => {
        client.sendKeyEvent(1, key);
        return false;
    };
    keyboard.onkeyup = (key) => client.sendKeyEvent(0, key);
    client.onstatechange = (state) => {
        if (state === 3) session.report('Connected', true);
        else if (state === 5) session.end('Disconnected');
    };
    client.onerror = (error) => session.end(error.message || 'Remote login failed.');
    // Credentials use only the authenticated same-origin socket, never URL/audit data.
    client.connect(
        JSON.stringify({
            username: options.username,
            password: options.password,
            domain: options.domain,
            privateKey: options.privateKey,
            width: surface.clientWidth,
            height: Math.max(240, surface.clientHeight),
            dpi: 96
        })
    );
}

function createTunnel(session, options, Guacamole) {
    const tunnel = new Guacamole.Tunnel();
    const parser = new Guacamole.Parser();
    const decoder = new TextDecoder();
    let socket;
    session.addCleanup(() => socket?.close());
    parser.oninstruction = (opcode, args) => tunnel.oninstruction?.(opcode, args);
    tunnel.sendMessage = (...parts) => {
        if (socket?.readyState === WebSocket.OPEN)
            socket.send(parts.map((value) => String(value).length + '.' + value).join(',') + ';');
    };
    tunnel.connect = (data) => {
        socket = new WebSocket(
            socketUrl(
                `/api/network/${options.id}/${options.tool}?port=${options.port}&direct=${options.direct ? 1 : 0}`
            )
        );
        socket.binaryType = 'arraybuffer';
        socket.onopen = () => {
            if (!session.isActive()) return socket.close();
            socket.send(data);
            tunnel.setUUID(crypto.randomUUID());
            tunnel.setState(Guacamole.Tunnel.State.OPEN);
        };
        socket.onmessage = (event) => {
            if (session.isActive())
                parser.receive(
                    typeof event.data === 'string'
                        ? event.data
                        : decoder.decode(new Uint8Array(event.data), { stream: true })
                );
        };
        socket.onerror = () =>
            tunnel.onerror?.(new Guacamole.Status(0x0200, 'Remote protocol connection failed.'));
        socket.onclose = (event) => {
            session.end(event.reason || 'Disconnected');
            tunnel.setState(Guacamole.Tunnel.State.CLOSED);
        };
    };
    tunnel.disconnect = () => {
        socket?.close();
        tunnel.setState(Guacamole.Tunnel.State.CLOSED);
    };
    return tunnel;
}
