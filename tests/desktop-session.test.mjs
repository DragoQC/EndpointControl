import test from 'node:test';
import assert from 'node:assert/strict';
import { DesktopSession } from '../src/MeshAll.Server/wwwroot/desktop-session.mjs';

function fixture() {
    const timers = [],
        reports = [],
        inputs = [],
        transports = [];
    let access = { status: 200, online: true, controlAllowed: true };
    const driver = new DesktopSession({
        controlRequested: true,
        controlAllowed: true,
        isActive: () => true,
        access: async () => ({ status: access.status, ok: access.status === 200, json: async () => access }),
        inputChanged: (...state) => inputs.push(state),
        report: (...state) => reports.push(state),
        fit: () => {},
        delay: (callback, ms) => {
            const timer = { callback, ms };
            timers.push(timer);
            return timer;
        },
        cancelDelay: (timer) => {
            timer.cancelled = true;
        },
        createDesktop: () => ({
            State: 0,
            mouse: false,
            keys: false,
            GrabMouseInput() {
                this.mouse = true;
            },
            GrabKeyInput() {
                this.keys = true;
            },
            UnGrabMouseInput() {
                this.mouse = false;
            },
            UnGrabKeyInput() {
                this.keys = false;
            }
        }),
        createRedirect: (desktop) => {
            const redirect = {
                Start() {
                    desktop.State = 3;
                    this.onStateChanged(this, 3);
                },
                Stop() {
                    desktop.State = 0;
                    desktop.UnGrabMouseInput();
                    desktop.UnGrabKeyInput();
                    this.onStateChanged?.(this, 0);
                },
                frame() {
                    desktop.onPreDrawImage();
                },
                drop() {
                    desktop.State = 0;
                    desktop.UnGrabMouseInput();
                    desktop.UnGrabKeyInput();
                    this.onStateChanged?.(this, 0);
                }
            };
            transports.push(redirect);
            return redirect;
        }
    });
    return {
        driver,
        timers,
        reports,
        inputs,
        transports,
        setAccess: (value) => {
            access = { ...access, ...value };
        },
        async retry() {
            const timer = timers.shift();
            assert.ok(timer && !timer.cancelled);
            timer.callback();
            await new Promise((resolve) => setImmediate(resolve));
        }
    };
}
test('control becomes available only with a real frame and returns automatically after a desktop switch', async () => {
    const f = fixture();
    await f.driver.attempt();
    assert.equal(f.driver.connected, false);
    assert.equal(f.driver.desktop.mouse, false);
    f.transports[0].frame();
    assert.deepEqual(f.inputs.at(-1), [true, true, true]);
    assert.equal(f.driver.desktop.keys, true);
    f.transports[0].drop();
    assert.deepEqual(f.inputs.at(-1), [true, false, false]);
    assert.equal(f.reports.at(-1)[1], true);
    await f.retry();
    f.transports[1].frame();
    assert.equal(f.driver.requested, true);
    assert.equal(f.driver.desktop.mouse, true);
    assert.equal(f.driver.desktop.keys, true);
});
test('an explicitly chosen view-only mode survives transport recovery', async () => {
    const f = fixture();
    await f.driver.attempt();
    f.transports[0].frame();
    f.driver.setControl(false);
    f.transports[0].drop();
    await f.retry();
    f.transports[1].frame();
    assert.equal(f.driver.desktop.stopInput, true);
    assert.deepEqual(f.inputs.at(-1), [true, true, false]);
});
test('an explicit Windows desktop transition refreshes the transport and keeps control requested', async () => {
    const f = fixture();
    await f.driver.attempt();
    f.transports[0].frame();
    f.driver.desktopChanged();
    assert.equal(f.reports.at(-1)[1], true);
    await f.retry();
    f.transports[1].frame();
    assert.equal(f.driver.desktop.keys, true);
    assert.equal(f.driver.requested, true);
});
test('the current server capability can remove input permission on reconnect', async () => {
    const f = fixture();
    await f.driver.attempt();
    f.transports[0].frame();
    f.transports[0].drop();
    f.setAccess({ controlAllowed: false });
    await f.retry();
    f.transports[1].frame();
    assert.equal(f.driver.desktop.mouse, false);
    assert.equal(f.driver.desktop.keys, false);
    assert.deepEqual(f.inputs.at(-1), [false, false, false]);
});
test('an offline agent is retried with backoff without leaving the viewer', async () => {
    const f = fixture();
    f.setAccess({ online: false });
    await f.driver.attempt();
    assert.equal(f.reports.at(-1)[1], true);
    await f.retry();
    assert.ok(f.timers[0].ms >= 1000);
    f.setAccess({ online: true });
    await f.retry();
    f.transports[0].frame();
    assert.equal(f.driver.connected, true);
});
for (const status of [401, 403, 404])
    test(`HTTP ${status} stops recovery`, async () => {
        const f = fixture();
        f.setAccess({ status });
        await f.driver.attempt();
        assert.equal(f.driver.stopped, true);
        assert.equal(f.timers.length, 0);
        assert.equal(f.reports.at(-1)[1], false);
    });
test('local sharing-bar close and consent rejection do not automatically reconnect', async () => {
    const f = fixture();
    await f.driver.attempt();
    f.transports[0].frame();
    f.driver.remoteEnded('local-user');
    assert.equal(f.driver.stopped, true);
    assert.equal(f.timers.length, 0);
    const g = fixture();
    await g.driver.attempt();
    g.transports[0].consoleMessageId = 2;
    g.transports[0].consoleMessage = 'Denied';
    g.transports[0].onConsoleMessageChange();
    assert.equal(g.driver.stopped, true);
    assert.equal(g.timers.length, 0);
});
test('explicit disconnect cancels pending retries', async () => {
    const f = fixture();
    f.setAccess({ online: false });
    await f.driver.attempt();
    const timer = f.timers[0];
    f.driver.stop();
    assert.equal(timer.cancelled, true);
    timer.callback();
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(f.transports.length, 0);
});
test('a dropped transport while approval is pending never causes repeated approval popups', async () => {
    const f = fixture();
    f.setAccess({ requiresConsent: true });
    await f.driver.attempt();
    f.transports[0].drop();
    assert.equal(f.driver.stopped, true);
    assert.equal(f.timers.length, 0);
    assert.equal(f.reports.at(-1)[1], false);
});
for (const reason of ['consent-denied', 'consent-timeout', 'consent-unavailable'])
    test(`${reason} stops recovery without creating another approval request`, async () => {
        const f = fixture();
        await f.driver.attempt();
        f.driver.remoteEnded(reason);
        assert.equal(f.driver.stopped, true);
        assert.equal(f.driver.connected, false);
        assert.equal(f.timers.length, 0);
        assert.equal(f.reports.at(-1)[1], false);
    });
