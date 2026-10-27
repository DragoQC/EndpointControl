const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');

function fixture() {
    const sessions = new EventEmitter();
    let users = {
        2: { SessionId: 2, State: 'Active', Username: 'alex', Domain: 'PC' },
        4: { SessionId: 4, State: 'Connected' }
    };
    sessions.Current = () => users;
    sessions.consoleUid = () => 4;
    const prompts = [],
        replies = [],
        timers = [];
    let now = 1000;
    let fail = false;
    const module = { exports: {} };
    vm.runInNewContext(
        fs.readFileSync('components/agent-core/modules_meshcore/meshall-screen-consent.js', 'utf8'),
        {
            module,
            process: { platform: 'win32' },
            Date: { now: () => now },
            setTimeout(callback, ms) {
                const timer = { callback, ms, active: true };
                timers.push(timer);
                return timer;
            },
            clearTimeout(timer) {
                timer.active = false;
            },
            require(name) {
                if (name === 'user-sessions') return sessions;
                if (name === 'meshall-consent-dialog')
                    return {
                        create(...args) {
                            if (fail) throw Error('UI failed');
                            const prompt = {
                                args,
                                closed: false,
                                close() {
                                    this.closed = true;
                                },
                                then(yes, no) {
                                    this.yes = yes;
                                    this.no = no;
                                }
                            };
                            prompts.push(prompt);
                            return prompt;
                        }
                    };
                throw Error('Unexpected approval dependency: ' + name);
            }
        }
    );
    const request = (nonce = 'a'.repeat(32), overrides = {}) => {
        const data = {
            sessionid: 'session',
            responseid: 'response',
            nonce,
            protocol: 'desktop',
            rights: 8,
            username: 'admin',
            ...overrides
        };
        module.exports.request(data, (reply) => replies.push(reply));
        return data;
    };
    return {
        api: module.exports,
        sessions,
        prompts,
        replies,
        timers,
        request,
        users(value) {
            users = value;
        },
        fail() {
            fail = true;
        },
        advance(ms) {
            now += ms;
        }
    };
}

test('prompts the real active user when the console is only a logon session; no avatar dependency', () => {
    const f = fixture();
    f.request();
    assert.equal(f.prompts.length, 1);
    assert.equal(f.prompts[0].args[4], 2);
    assert.equal(f.replies.length, 0);
    assert.equal(f.timers[0].ms, 30000);
});

test('approval is bound to one session, protocol and actor, and is consumed once', () => {
    const f = fixture();
    const request = f.request();
    f.prompts[0].yes();
    assert.equal(f.replies[0].granted, true);
    assert.equal(f.replies[0].targetSession, 2);
    assert.ok(f.api.consume(request.nonce, 'session', 'desktop', 'admin'));
    assert.equal(f.api.consume(request.nonce, 'session', 'desktop', 'admin'), null);
    f.request('b'.repeat(32));
    f.prompts[1].yes();
    assert.equal(f.api.consume('b'.repeat(32), 'different-session', 'desktop', 'admin'), null);
});

test('denial, timeout and late acceptance never produce a usable approval', () => {
    for (const outcome of ['deny', 'timeout']) {
        const f = fixture();
        const request = f.request();
        if (outcome === 'deny') f.prompts[0].no();
        else f.timers[0].callback();
        f.prompts[0].yes();
        assert.equal(f.replies.length, 1);
        assert.equal(f.replies[0].granted, false);
        assert.equal(f.prompts[0].closed, true);
        assert.equal(f.api.consume(request.nonce, 'session', 'desktop', 'admin'), null);
    }
});

test('missing/ambiguous users and prompt failures deny without opening screen access', () => {
    for (const kind of ['missing', 'ambiguous', 'failure']) {
        const f = fixture();
        if (kind === 'missing') f.users({});
        if (kind === 'ambiguous')
            f.users({
                2: { SessionId: 2, State: 'Active', Username: 'a' },
                3: { SessionId: 3, State: 'Active', Username: 'b' }
            });
        if (kind === 'failure') f.fail();
        f.request();
        assert.equal(f.replies[0].granted, false);
        assert.equal(f.replies[0].reason, 'unavailable');
    }
});

test('cancel and user-session changes close pending dialogs and invalidate approved grants', () => {
    const f = fixture();
    const request = f.request();
    f.api.cancel(request);
    assert.equal(f.prompts[0].closed, true);
    assert.equal(f.replies[0].granted, false);
    f.request('b'.repeat(32));
    f.prompts[1].yes();
    f.sessions.emit('changed');
    assert.equal(f.api.consume('b'.repeat(32), 'session', 'desktop', 'admin'), null);
});

test('every new screen request prompts again and expired grants fail closed', () => {
    const f = fixture();
    const request = f.request();
    f.prompts[0].yes();
    f.advance(16000);
    assert.equal(f.api.consume(request.nonce, 'session', 'desktop', 'admin'), null);
    f.request('b'.repeat(32));
    assert.equal(f.prompts.length, 2);
    assert.equal(f.replies.length, 1);
});
