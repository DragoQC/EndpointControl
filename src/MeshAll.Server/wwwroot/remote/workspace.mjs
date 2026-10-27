/** Layout/fullscreen live for the mounted view, not for a particular connection. */
export class RemoteWorkspace {
    constructor(root) {
        this.root = root;
        this.abort = new AbortController();
        const update = () => this.update();
        window.addEventListener('resize', update, { signal: this.abort.signal });
        document.addEventListener('fullscreenchange', update, { signal: this.abort.signal });
        if (root.classList.contains('remote-inline')) {
            this.observer = new ResizeObserver(update);
            this.observer.observe(document.body);
        }
        this.update();
    }

    update() {
        const root = this.root;
        if (!root.isConnected) return;
        const fullscreen = document.fullscreenElement === root;
        root.querySelector('[data-remote-fullscreen]')?.setAttribute('aria-pressed', String(fullscreen));
        if (!fullscreen && root.classList.contains('remote-inline')) {
            const top = Math.round(root.getBoundingClientRect().top + window.scrollY) + 'px';
            if (root.style.getPropertyValue('--remote-inline-top') !== top)
                root.style.setProperty('--remote-inline-top', top);
        }
    }

    async dispose() {
        this.abort.abort();
        this.observer?.disconnect();
        if (document.fullscreenElement === this.root) {
            try {
                await document.exitFullscreen();
            } catch {
                // The browser may already be removing this document/element.
            }
        }
    }
}
