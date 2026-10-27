/** Owns one connection's callbacks, listeners and resources, independently of fullscreen. */
export class RemoteSession {
    constructor(owner, root, dotnet, isCurrent, controlRequested) {
        this.owner = owner;
        this.root = root;
        this.dotnet = dotnet;
        this.isCurrent = isCurrent;
        this.control = controlRequested;
        this.abort = new AbortController();
        this.cleanups = [];
    }

    isActive() {
        return !this.abort.signal.aborted && this.isCurrent();
    }

    notify(method, ...args) {
        if (this.isActive()) this.dotnet.invokeMethodAsync(method, ...args).catch(() => {});
    }

    report(text, connected) {
        this.notify('StateAsync', text, connected);
    }

    end(text) {
        this.report(text, false);
        this.dispose();
    }

    inputState(allowed, available, enabled) {
        const key = [allowed, available, enabled].join(':');
        if (!this.isActive() || key === this.lastInputState) return;
        this.lastInputState = key;
        this.notify('DesktopInputStateAsync', allowed, available, enabled);
    }

    addCleanup(cleanup) {
        if (this.abort.signal.aborted) this.release(cleanup);
        else this.cleanups.push(cleanup);
    }

    listen(target, type, listener, options = {}) {
        target.addEventListener(type, listener, { ...options, signal: this.abort.signal });
    }

    observeSize(elements, fit) {
        const update = () => {
            if (this.isActive()) fit();
        };
        const observer = new ResizeObserver(update);
        this.addCleanup(() => observer.disconnect());
        for (const element of elements) observer.observe(element);
        this.listen(window, 'resize', update);
        this.listen(document, 'fullscreenchange', update);
        requestAnimationFrame(update);
    }

    dispose() {
        if (this.abort.signal.aborted) return;
        this.abort.abort();
        // Unwind in reverse registration order; one failed release must not leave
        // the remaining keyboard handlers, sockets or capture resources alive.
        for (const cleanup of this.cleanups.reverse()) this.release(cleanup);
        this.cleanups.length = 0;
    }

    release(cleanup) {
        try {
            cleanup();
        } catch {
            // Cleanup is best-effort after transport/browser teardown.
        }
    }
}
