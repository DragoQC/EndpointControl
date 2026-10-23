// Keep the viewer and requested control mode alive while Windows changes desktop.
export class DesktopSession {
    constructor(options) {
        Object.assign(this, options);
        this.requested = !!options.controlRequested;
        this.allowed = !!options.controlAllowed;
        this.connected = false;
        this.stopped = false;
        this.generation = 0;
        this.failures = 0;
        this.delay = options.delay || ((callback, ms) => setTimeout(callback, ms));
        this.cancelDelay = options.cancelDelay || clearTimeout;
    }
    valid() { return !this.stopped && this.isActive(); }
    notifyInput() {
        const enabled = this.allowed && this.connected && this.requested;
        const key = [this.allowed, this.connected, enabled].join(':');
        if (key !== this.lastInput) {
            this.lastInput = key;
            this.inputChanged(this.allowed, this.allowed && this.connected, enabled);
        }
    }
    applyInput() {
        const desktop = this.desktop;
        const enabled = this.allowed && this.connected && this.requested && desktop?.State === 3;
        if (desktop) {
            desktop.stopInput = !enabled;
            if (enabled) { desktop.GrabMouseInput(); desktop.GrabKeyInput(); }
            else { desktop.handleReleaseKeys?.(); desktop.UnGrabMouseInput(); desktop.UnGrabKeyInput(); }
        }
        this.notifyInput();
    }
    setControl(value) { this.requested = !!value; this.applyInput(); }
    closeTransport() {
        this.generation++;
        this.connected = false;
        const redirect = this.redirect;
        this.redirect = null;
        if (redirect) { redirect.onStateChanged = null; redirect.onConsoleMessageChange = null; redirect.Stop(); }
        this.desktop?.UnGrabMouseInput();
        this.desktop?.UnGrabKeyInput();
        this.desktop = null;
        this.notifyInput();
    }
    queue(message) {
        if (!this.valid() || this.timer) return;
        this.connected = false;
        this.applyInput();
        this.report(message, true);
        const timeout = Math.min(10000, 500 * 2 ** Math.min(this.failures++, 5));
        this.timer = this.delay(() => { this.timer = null; this.attempt(); }, timeout);
    }
    end(message) {
        this.stopped = true;
        if (this.timer) this.cancelDelay(this.timer);
        this.timer = null;
        this.closeTransport();
        this.report(message, false);
    }
    async attempt() {
        if (!this.valid()) return;
        this.closeTransport();
        const generation = this.generation;
        try {
            const response = await this.access();
            if (!this.valid() || generation !== this.generation) return;
            if ([401, 403, 404].includes(response.status)) {
                this.end(response.status === 401 ? 'Your sign-in expired. Sign in again.' : 'Desktop access is no longer available.'); return;
            }
            if (!response.ok) { this.queue('Connection interrupted. Reconnecting automatically…'); return; }
            const capabilities = await response.json();
            if (!this.valid() || generation !== this.generation) return;
            this.allowed = !!capabilities.controlAllowed;
            this.notifyInput();
            if (!capabilities.online) { this.queue('Waiting for the agent. Reconnecting automatically…'); return; }
            this.report(this.failures ? 'Restoring the desktop session…' : 'Connecting…', true);
            const desktop = this.createDesktop();
            this.desktop = desktop;
            desktop.preserveFrameOnStop = true;
            desktop.stopInput = true;
            desktop.onScreenSizeChange = () => { if (this.valid() && generation === this.generation) { this.fit(); this.applyInput(); } };
            desktop.onPreDrawImage = () => {
                if (!this.valid() || generation !== this.generation) return;
                const first = !this.connected;
                this.connected = true; this.failures = 0;
                if (first) { this.applyInput(); this.report('Connected', true); }
            };
            const redirect = this.createRedirect(desktop);
            this.redirect = redirect;
            redirect.onStateChanged = (_, state) => {
                if (!this.valid() || generation !== this.generation) return;
                if (state === 3) { this.applyInput(); this.report('Waiting for the desktop frame…', true); }
                else if (state === 0) { this.connected = false; this.applyInput(); this.queue('Desktop changed or connection interrupted. Reconnecting automatically…'); }
            };
            redirect.onConsoleMessageChange = () => {
                if (!this.valid() || generation !== this.generation) return;
                if (redirect.consoleMessageId === 2) { this.end(redirect.consoleMessage || 'Desktop access was declined.'); return; }
                if (redirect.consoleMessage) this.report(redirect.consoleMessage, true);
                else if (this.connected) this.report('Connected', true);
            };
            redirect.Start();
        } catch (error) {
            if (this.valid() && generation === this.generation) this.queue('Connection interrupted. Reconnecting automatically…');
        }
    }
    remoteEnded(reason) { this.end(reason === 'local-user' ? 'The person at the computer ended desktop sharing.' : 'Desktop sharing ended.'); }
    stop() {
        this.stopped = true;
        if (this.timer) this.cancelDelay(this.timer);
        this.timer = null;
        this.closeTransport();
    }
}
