const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');

function fixture(branded = false) {
    const child = new EventEmitter();
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    const childInput = new EventEmitter();
    const pendingInput = [];
    let childReady = false;
    const timers = [];
    const intervals = [],
        branding = [];
    child.stdin = {
        write(data) {
            if (childReady) childInput.emit('data', data);
            else pendingInput.push(data);
        }
    };
    child.kill = () => {
        child.killed = true;
    };
    let dispatched, flags, answer, failure;
    function NativePromise() {
        this.resolve = () => {
            this.allowed = true;
        };
        this.reject = (reason) => {
            this.allowed = false;
            this.reason = reason;
        };
    }
    NativePromise.defaultInit = () => {};
    const module = { exports: {} };
    vm.runInNewContext(
        fs.readFileSync('components/agent-core/modules_meshcore/meshall-consent-dialog.js', 'utf8'),
        {
            module,
            Buffer,
            process: { execPath: 'C:\\Program Files\\MeshAllAgent.exe' },
            setTimeout() {},
            require(name) {
                if (name === 'promise') return NativePromise;
                if (name === 'meshall-consent-logo')
                    return require('../components/agent-core/modules_meshcore/meshall-consent-logo.js');
                if (name === 'child_process')
                    return {
                        execFile(file, args, options) {
                            dispatched = { file, args, options };
                            return child;
                        }
                    };
                throw Error('Unexpected dependency: ' + name);
            }
        }
    );
    const result = module.exports.create('Approval', 'Allow this screen session?', 30, null, 2);
    vm.runInNewContext(Buffer.from(dispatched.args[2], 'base64').toString(), {
        process: {
            pid: 123,
            stdout: {
                write(value) {
                    child.stdout.emit('data', Buffer.from(value));
                }
            },
            stdin: childInput,
            _exit() {
                child.killed = true;
                child.emit('exit');
            }
        },
        Buffer,
        setInterval(callback) {
            intervals.push(callback);
            return callback;
        },
        clearInterval() {},
        setTimeout(callback, ms) {
            timers.push({ callback, ms });
        },
        require(name) {
            if (name === '_GenericMarshal')
                return {
                    CreateVariable(value) {
                        if (branded && typeof value === 'number') {
                            const data = Buffer.alloc(value);
                            return { toBuffer: () => data };
                        }
                        return value;
                    },
                    CreateNativeProxy() {
                        return {
                            CreateMethod() {},
                            CreateIconFromResourceEx(_buffer, _length, _icon, _version, width) {
                                return { Val: width === 32 ? 320 : 160 };
                            },
                            FindWindowExW(_parent, previous) {
                                return { Val: previous ? 0 : 100 };
                            },
                            GetWindowThreadProcessId(_window, pid) {
                                pid.toBuffer().writeUInt32LE(123);
                            },
                            GetDlgItem() {
                                return { Val: 200 };
                            },
                            SendMessageW(...args) {
                                branding.push(args.map((value) => value?.Val ?? value));
                            },
                            MessageBoxW: {
                                async(_owner, _caption, _title, value) {
                                    flags = value;
                                    return {
                                        then(ok, failed) {
                                            answer = ok;
                                            failure = failed;
                                        }
                                    };
                                }
                            }
                        };
                    }
                };
            throw Error('Unexpected child dependency: ' + name);
        }
    });
    childReady = true;
    pendingInput.forEach((data) => childInput.emit('data', data));
    return {
        child,
        result,
        dispatched,
        flags,
        timers,
        intervals,
        branding,
        answer: (value) => answer({ Val: value }),
        fail: () => failure()
    };
}

test('the native dialog permits Cancel/close and defaults to No', () => {
    const f = fixture();
    assert.equal(f.flags & 15, 3);
    assert.equal(f.flags & 0x300, 0x100);
    assert.ok(f.flags & 0x40000);
});

for (const value of [2, 7, 0])
    test(`native decision ${value} cannot grant approval`, () => {
        const f = fixture();
        f.answer(value);
        assert.equal(f.result.allowed, false);
    });

test('only native Yes approves; child failure or exit cannot approve', () => {
    const yes = fixture();
    yes.answer(6);
    assert.equal(yes.result.allowed, true);
    const failed = fixture();
    failed.fail();
    assert.equal(failed.result.allowed, false);
    const exited = fixture();
    exited.child.emit('exit');
    exited.answer(6);
    assert.equal(exited.result.allowed, false);
});

test('the self-contained child runs in the approved session and closes idempotently', () => {
    const f = fixture();
    assert.equal(f.dispatched.options.uid, 2);
    assert.equal(f.dispatched.options.type, 1);
    assert.equal(f.dispatched.args[1], '-b64exec');
    assert.ok(
        f.dispatched.args[2].length < 3500,
        'native launcher buffer must not include logo/text payloads'
    );
    f.result.close();
    f.result.close();
    assert.equal(f.child.killed, true);
});

test('the child has its own bounded watchdog if parent cleanup cannot run', () => {
    const f = fixture();
    const watchdog = f.timers.find((timer) => timer.ms === 31000);
    assert.ok(watchdog);
    watchdog.callback();
    assert.equal(f.child.killed, true);
    assert.equal(f.result.allowed, false);
});

test('EndpointControl branding uses the shared 48px logo and only modifies the owned dialog', () => {
    const png = Buffer.from(
        require('../components/agent-core/modules_meshcore/meshall-consent-logo.js'),
        'base64'
    );
    assert.equal(png.readUInt32BE(16), 48);
    assert.equal(png.readUInt32BE(20), 48);
    const f = fixture(true);
    f.intervals[0]();
    assert.ok(f.branding.some((call) => call[0] === 100 && call[1] === 0x80));
    assert.ok(f.branding.some((call) => call[0] === 200 && call[1] === 0x170 && call[2] === 320));
    assert.equal(f.result.allowed, undefined);
});
