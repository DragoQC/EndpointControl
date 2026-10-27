import assert from 'node:assert/strict';
import test from 'node:test';
import { RemoteSession } from '../src/MeshAll.Server/wwwroot/remote/session.mjs';

test('replaced/disposed connections cannot publish status or input into a new view', () => {
    const calls = [];
    const dotnet = {
        invokeMethodAsync: (...args) => {
            calls.push(args);
            return Promise.resolve();
        }
    };
    let current;
    const first = new RemoteSession('first', {}, dotnet, () => current === first, true);
    const second = new RemoteSession('second', {}, dotnet, () => current === second, true);
    current = first;
    first.report('Connected', true);
    current = second;
    first.report('Disconnected', false);
    first.inputState(true, true, true);
    second.inputState(true, true, true);
    second.inputState(true, true, true);
    first.dispose();
    first.report('Connected', true);
    assert.deepEqual(calls, [
        ['StateAsync', 'Connected', true],
        ['DesktopInputStateAsync', true, true, true]
    ]);
});

test('cleanup is idempotent, aborts listeners and releases every resource despite a failed release', () => {
    const releases = [];
    const session = new RemoteSession('owner', {}, {}, () => true, true);
    const target = new EventTarget();
    let inputs = 0;
    session.listen(target, 'input', () => inputs++);
    target.dispatchEvent(new Event('input'));
    session.addCleanup(() => releases.push('socket'));
    session.addCleanup(() => {
        throw Error('already closed');
    });
    session.addCleanup(() => releases.push('keyboard'));
    session.dispose();
    session.dispose();
    target.dispatchEvent(new Event('input'));
    session.addCleanup(() => releases.push('late resource'));
    assert.equal(inputs, 1);
    assert.deepEqual(releases, ['keyboard', 'socket', 'late resource']);
    assert.equal(session.isActive(), false);
});

test('a terminal failure is reported once and late close/frame callbacks cannot overwrite it', () => {
    const calls = [];
    const dotnet = {
        invokeMethodAsync: (...args) => {
            calls.push(args);
            return Promise.resolve();
        }
    };
    const session = new RemoteSession('owner', {}, dotnet, () => true, true);
    let closed = 0;
    session.addCleanup(() => closed++);
    session.end('Terminal connection failed.');
    session.end('Disconnected');
    session.report('Connected', true);
    assert.deepEqual(calls, [['StateAsync', 'Terminal connection failed.', false]]);
    assert.equal(closed, 1);
});
