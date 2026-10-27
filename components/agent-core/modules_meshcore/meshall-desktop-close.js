/* MeshAll desktop stop lifecycle. Copyright DragoQC and contributors. MIT. */
// Stop input/capture immediately. The viewer acknowledges the local-stop message
// by closing its tunnel; tearing KVM down on write completion can discard the
// message still buffered below the native WebSocket stream.
module.exports = function (kvm) {
    if (!kvm || kvm._meshallLocalClosed) return;
    kvm._meshallLocalClosed = true;
    var streams = (kvm.tunnels || []).slice();
    var piped = kvm._pipedStreams || [];
    for (var i = 0; i < piped.length; i++) {
        if (streams.indexOf(piped[i]) < 0) streams.push(piped[i]);
    }
    for (var i = 0; i < streams.length; i++) {
        try {
            streams[i].unpipe(kvm);
        } catch (ex) {}
        try {
            kvm.unpipe(streams[i]);
        } catch (ex) {}
        // Native unpipe() pauses the readable side and removes the pipe on the
        // next turn. Resume afterward so the peer's end/close can still arrive;
        // its input is now detached and cannot reach KVM.
        (function (stream) {
            setImmediate(function () {
                try {
                    stream.resume();
                } catch (ex) {}
            });
        })(streams[i]);
    }
    var remaining = streams.length,
        closed = false,
        timer = null;
    var pending = streams.map(function () {
        return true;
    });
    function finish() {
        if (closed) return;
        closed = true;
        if (timer) clearTimeout(timer);
        for (var i = 0; i < streams.length; i++) {
            if (pending[i]) {
                try {
                    streams[i].end();
                } catch (ex) {}
            }
        }
        // An acknowledged tunnel has already run tunnel_kvm_end. Ending it a
        // second time corrupts the shared connection count for the next viewer.
        if (remaining > 0 && kvm.connectionCount !== 0) {
            try {
                kvm.end();
            } catch (ex) {}
        }
    }
    function notify(stream, index) {
        function acknowledged() {
            if (pending[index]) {
                pending[index] = false;
                if (--remaining == 0) finish();
            }
        }
        try {
            stream.once('end', acknowledged);
            stream.write(
                JSON.stringify({ ctrlChannel: '102938', type: 'meshall-desktop-ended', reason: 'local-user' })
            );
        } catch (ex) {
            /* Keep the failed notification pending for fallback cleanup. */
        }
    }
    if (!remaining) {
        try {
            kvm.end();
        } catch (ex) {}
        return;
    }
    // Older or unresponsive viewers still get bounded cleanup. Their desktop
    // input and capture pipes have already been detached above.
    timer = setTimeout(finish, 5000);
    for (var i = 0; i < streams.length; i++) notify(streams[i], i);
};
