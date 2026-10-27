const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');

const source = fs.readFileSync(
    path.join(__dirname, '../components/agent-core/modules_meshcore/meshall-inventory.js'),
    'utf8'
);

function fixture(platform) {
    const files = {
        '/etc/os-release': 'PRETTY_NAME="Linux fixture"',
        '/proc/cpuinfo': 'processor : 0\nprocessor : 1\nmodel name : Test CPU',
        '/proc/meminfo': 'MemTotal: 16384 kB\nMemAvailable: 4096 kB',
        '/proc/stat': 'cpu 100 0 100 700 100 0 0 0 999 0'
    };
    const children = [],
        timeouts = [],
        intervals = [],
        pendingCpu = [],
        reports = [];
    const systemInfo = {
        memUtilization: () => ({ MemTotal: null, MemFree: null }),
        cpuUtilization: () => ({ then: (resolve, reject) => pendingCpu.push({ resolve, reject }) })
    };
    function childProcess() {
        const child = new EventEmitter();
        child.stdout = new EventEmitter();
        child.stderr = new EventEmitter();
        child.kill = () => {
            child.killed = true;
            child.emit('exit', 1);
        };
        children.push(child);
        return child;
    }
    const context = {
        module: { exports: {} },
        process: { platform, arch: 'x64', versions: { meshAgent: 'fixture' } },
        setTimeout: (callback, milliseconds) => {
            const timeout = { callback, milliseconds };
            timeouts.push(timeout);
            return timeout;
        },
        clearTimeout: (timeout) => {
            timeout.cancelled = true;
        },
        setInterval: (callback, milliseconds) => {
            const interval = { callback, milliseconds };
            intervals.push(interval);
            return interval;
        },
        clearInterval: (interval) => {
            interval.cancelled = true;
        },
        require: (name) => {
            if (name === 'fs')
                return {
                    readFileSync: (file) => {
                        if (!(file in files)) throw new Error('No fixture file');
                        return Buffer.from(files[file]);
                    }
                };
            if (name === 'os')
                return { hostname: () => 'fixture', uptime: () => 100, networkInterfaces: () => ({}) };
            if (name === 'child_process') return { execFile: childProcess };
            if (name === 'sysinfo') return systemInfo;
            if (name === 'win-wmi-fixed') return { query: () => [] };
            throw new Error('Unexpected native module: ' + name);
        }
    };
    vm.runInNewContext(source, context);
    const collector = context.module.exports;
    return {
        collector,
        files,
        children,
        timeouts,
        intervals,
        pendingCpu,
        reports,
        systemInfo,
        start: () =>
            collector.start(
                (report) => reports.push(report.inventory),
                () => []
            ),
        finishLinux(exitCode = 0) {
            const child = children[children.length - 1];
            child.stdout.emit(
                'data',
                Buffer.from(
                    'x86_64\nFilesystem 1024-blocks Used Available Capacity Mounted on\n/dev/root 1000 990 10 99% /\n/dev/root 1000 990 10 99% /bind-root\n'
                )
            );
            child.emit('exit', exitCode);
        }
    };
}

test('Linux CPU waits for a delta and ignores guest time already included in other counters', () => {
    const native = fixture('linux');
    native.start();
    native.finishLinux();
    assert.equal(native.reports[0].cpu, null);
    assert.equal(native.reports[0].memoryTotal, 16384 * 1024);
    assert.equal(native.reports[0].memoryUsed, 12288 * 1024);
    assert.equal(native.reports[0].disks.length, 1, 'Bind mounts must not multiply filesystem capacity');
    assert.equal(native.intervals[0].milliseconds, 15000);

    native.files['/proc/stat'] = 'cpu 200 0 200 750 150 0 0 0 2000 0';
    native.collector.refresh();
    native.finishLinux();
    assert.ok(Math.abs(native.reports[1].cpu - (100 * 2) / 3) < 0.0001);
    native.collector.stop();
});

test('Invalid Windows memory stays unavailable while a real zero CPU sample is preserved', () => {
    const native = fixture('win32');
    native.start();
    native.pendingCpu[0].resolve({ total: 0 });
    assert.equal(native.reports[0].cpu, 0);
    assert.equal(native.reports[0].memoryTotal, undefined);
    assert.equal(native.reports[0].memoryUsed, undefined);
    native.collector.stop();
});

test('One inaccessible Linux mount cannot discard other valid filesystem readings', () => {
    const native = fixture('linux');
    native.start();
    native.finishLinux(1);
    assert.equal(native.reports[0].disks.length, 1);
    assert.equal(native.reports[0].disks[0].name, '/');
    assert.equal(native.reports[0].disks[0].free, 10 * 1024);
    native.collector.stop();
});

test('Stopping and restarting the collector cannot publish a previous in-flight sample', () => {
    const native = fixture('win32');
    native.start();
    native.collector.stop();
    native.start();
    native.pendingCpu[0].resolve({ total: 99 });
    assert.equal(native.reports.length, 0);
    native.pendingCpu[1].resolve({ total: 12.5 });
    assert.equal(native.reports.length, 1);
    assert.equal(native.reports[0].cpu, 12.5);
    native.collector.stop();
});

test('A timed-out storage probe publishes partial data once and releases the child', () => {
    const native = fixture('linux');
    native.start();
    const deadline = native.timeouts.find((timeout) => timeout.milliseconds === 10000);
    deadline.callback();
    assert.equal(native.children[0].killed, true);
    assert.equal(native.reports.length, 1);
    assert.equal(native.reports[0].disks, undefined);
    assert.match(native.reports[0].diagnostic, /timed out/);
    native.finishLinux();
    assert.equal(native.reports.length, 1);
    native.collector.stop();
});
