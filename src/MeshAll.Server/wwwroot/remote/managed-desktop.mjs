import { fitCanvas, socketUrl } from './assets.mjs';

export function connectManagedDesktop(session, options) {
    const canvas = session.root.querySelector('#remote-canvas');
    const socket = new WebSocket(socketUrl(`/api/remote/${options.id}/desktop`));
    session.addCleanup(() => socket.close());
    session.observeSize([canvas.parentElement], () => fitCanvas(canvas));
    bindInput(session, canvas, socket);
    let painting = Promise.resolve();
    socket.onmessage = (event) => {
        if (!session.isActive()) return;
        const message = JSON.parse(event.data);
        if (message.type === 'status') session.report(message.message, true);
        else if (message.type === 'error') {
            session.end(message.message);
        } else if (message.type === 'frame') {
            painting = painting.then(() => paintFrame(session, canvas, options, message));
        }
    };
    socket.onclose = () => session.end('Disconnected');
    socket.onerror = () => session.end('Desktop connection failed.');
    session.sendCtrlAltDelete = () => {
        if (!session.control || socket.readyState !== WebSocket.OPEN) return;
        for (const key of [17, 18, 46]) socket.send(JSON.stringify({ type: 'keydown', key }));
        for (const key of [46, 18, 17]) socket.send(JSON.stringify({ type: 'keyup', key }));
    };
}

function paintFrame(session, canvas, options, frame) {
    return new Promise((resolve) => {
        const image = new Image();
        image.onload = () => {
            if (session.isActive()) {
                if (canvas.width !== frame.width || canvas.height !== frame.height) {
                    canvas.width = frame.width;
                    canvas.height = frame.height;
                }
                canvas.getContext('2d').drawImage(image, 0, 0);
                fitCanvas(canvas);
                session.inputState(!!options.controlAllowed, true, session.control);
                session.report('Connected', true);
            }
            resolve();
        };
        image.onerror = resolve;
        image.src = 'data:image/jpeg;base64,' + frame.base64;
    });
}

function bindInput(session, canvas, socket) {
    let lastMove = 0;
    const send = (value) => {
        if (session.isActive() && session.control && socket.readyState === WebSocket.OPEN)
            socket.send(JSON.stringify(value));
    };
    const pointer = (event, type) => {
        if (!session.control) return;
        event.preventDefault();
        const bounds = canvas.getBoundingClientRect();
        send({
            type,
            x: Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width)),
            y: Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height)),
            button: event.button
        });
    };
    session.listen(canvas, 'pointerdown', (event) => {
        canvas.focus();
        if (session.control) canvas.setPointerCapture(event.pointerId);
        pointer(event, 'down');
    });
    session.listen(canvas, 'pointerup', (event) => pointer(event, 'up'));
    session.listen(canvas, 'pointermove', (event) => {
        if (performance.now() - lastMove <= 40) return;
        lastMove = performance.now();
        pointer(event, 'move');
    });
    session.listen(canvas, 'contextmenu', (event) => {
        if (session.control) event.preventDefault();
    });
    session.listen(
        canvas,
        'wheel',
        (event) => {
            if (!session.control) return;
            event.preventDefault();
            send({ type: 'wheel', delta: event.deltaY > 0 ? -120 : 120 });
        },
        { passive: false }
    );
    for (const type of ['keydown', 'keyup'])
        session.listen(canvas, type, (event) => {
            if (!session.control) return;
            event.preventDefault();
            send({ type, key: event.keyCode });
        });
    session.listen(canvas, 'blur', () => send({ type: 'release' }));
}
