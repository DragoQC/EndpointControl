import { loadScript, loadStylesheet, socketUrl } from './assets.mjs';

export async function connectTerminal(session, options) {
    await loadScript('/vendor/xterm/xterm.js');
    loadStylesheet('/vendor/xterm/xterm.css');
    if (!session.isActive()) return;
    const container = session.root.querySelector('#remote-terminal');
    const terminal = new Terminal({
        cursorBlink: true,
        screenReaderMode: true,
        fontSize: 13,
        fontFamily: 'Consolas, monospace',
        theme: { background: '#12232d', foreground: '#d6e6e0' }
    });
    terminal.open(container);
    session.addCleanup(() => terminal.dispose());
    const socket = new WebSocket(socketUrl(`/api/network/${options.id}/${options.tool}`));
    socket.binaryType = 'arraybuffer';
    session.addCleanup(() => socket.close());
    let ready = false;
    const fit = () => {
        terminal.resize(
            Math.max(20, Math.floor(container.clientWidth / 8)),
            Math.max(5, Math.floor(container.clientHeight / 17))
        );
        if (socket.readyState === WebSocket.OPEN)
            socket.send(JSON.stringify({ type: 'resize', columns: terminal.cols, rows: terminal.rows }));
    };
    session.observeSize([container], fit);
    socket.onmessage = (event) => {
        if (!session.isActive()) return;
        if (typeof event.data === 'string' && event.data.startsWith('{')) {
            const message = JSON.parse(event.data);
            if (message.type === 'status') session.report(message.message, true);
            return;
        }
        if (!ready) {
            ready = true;
            session.report('Connected', true);
            terminal.focus();
            fit();
        }
        terminal.write(new Uint8Array(event.data));
    };
    terminal.onData((data) => {
        if (session.isActive() && ready && socket.readyState === WebSocket.OPEN)
            socket.send(new TextEncoder().encode(data));
    });
    socket.onclose = (event) => session.end(event.reason || 'Disconnected');
    socket.onerror = () => session.end('Terminal connection failed.');
}
