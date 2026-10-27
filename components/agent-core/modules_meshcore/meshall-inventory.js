/* MeshAll inventory collector. Copyright DragoQC and contributors. MIT license. */
// The retained native agent uses ES5 JavaScript. Keep var/function syntax here.
var fileSystem = require('fs');
var operatingSystem = require('os');
var refreshTimer = null;
var cancelCollection = null;
var collectionBusy = false;
var collectionGeneration = 0;
var previousCpuSample = null;
var sendInventory = null;
var getSignedInUsers = null;

function readTextFile(path) {
    try {
        return fileSystem.readFileSync(path).toString().trim();
    } catch (error) {
        return '';
    }
}

function nonNegativeNumber(value) {
    if (value == null || value === '') return null;
    var number = Number(value);
    return isFinite(number) && number >= 0 ? number : null;
}

function recordMemory(snapshot, total, available, unitSize) {
    total = nonNegativeNumber(total);
    available = nonNegativeNumber(available);
    if (total === null || total <= 0 || available === null || available > total) return;
    snapshot.memoryTotal = total * unitSize;
    snapshot.memoryUsed = (total - available) * unitSize;
}

function readInterfaces(snapshot) {
    try {
        var interfaces = [];
        var networks = operatingSystem.networkInterfaces();
        Object.keys(networks).forEach(function (name) {
            networks[name].forEach(function (network) {
                interfaces.push({
                    name: name,
                    address: network.address,
                    family: String(network.family),
                    mac: network.mac || '',
                    internal: !!network.internal
                });
            });
        });
        snapshot.interfaces = interfaces;
    } catch (error) {
        // A platform without interface enumeration can still report its resources.
    }
}

function collectWindows(snapshot, complete) {
    try {
        var wmi = require('win-wmi-fixed');
        var system = wmi.query(
            'ROOT\\CIMV2',
            'SELECT Manufacturer,Model,NumberOfLogicalProcessors,UserName FROM Win32_ComputerSystem',
            ['Manufacturer', 'Model', 'NumberOfLogicalProcessors', 'UserName']
        )[0];
        var windows = wmi.query(
            'ROOT\\CIMV2',
            'SELECT Caption,Version,BuildNumber,OSArchitecture,TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem',
            [
                'Caption',
                'Version',
                'BuildNumber',
                'OSArchitecture',
                'TotalVisibleMemorySize',
                'FreePhysicalMemory'
            ]
        )[0];
        var processors = wmi.query('ROOT\\CIMV2', 'SELECT Name,Architecture FROM Win32_Processor', [
            'Name',
            'Architecture'
        ]);

        if (system) {
            snapshot.manufacturer = system.Manufacturer;
            snapshot.model = system.Model;
            snapshot.cores = nonNegativeNumber(system.NumberOfLogicalProcessors);
            if (system.UserName) snapshot.users = [system.UserName];
        }
        if (windows) {
            snapshot.os = windows.Caption + ' ' + windows.Version + ' (build ' + windows.BuildNumber + ')';
            recordMemory(snapshot, windows.TotalVisibleMemorySize, windows.FreePhysicalMemory, 1024);
            if (windows.OSArchitecture) {
                snapshot.architecture = String(windows.OSArchitecture).indexOf('64') >= 0 ? 'x64' : 'x86';
            }
        }
        if (processors && processors.length) {
            snapshot.cpuModel = processors
                .map(function (processor) {
                    return processor.Name;
                })
                .join(' / ');
            if (Number(processors[0].Architecture) === 12) snapshot.architecture = 'arm64';
        }

        var disks = [];
        wmi.query('ROOT\\CIMV2', 'SELECT DeviceID,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3', [
            'DeviceID',
            'Size',
            'FreeSpace'
        ]).forEach(function (disk) {
            var total = nonNegativeNumber(disk.Size);
            var free = nonNegativeNumber(disk.FreeSpace);
            if (total > 0 && free !== null && free <= total) {
                disks.push({ name: disk.DeviceID, total: total, free: free });
            }
        });
        snapshot.disks = disks;
    } catch (error) {
        snapshot.diagnostic = 'Windows inventory query failed: ' + String(error);
    }

    // Native PDH counters work independently of WMI inventory queries.
    try {
        var systemInfo = require('sysinfo');
        var memory = systemInfo.memUtilization();
        recordMemory(snapshot, memory.MemTotal, memory.MemFree, 1);
        systemInfo.cpuUtilization().then(function (cpu) {
            var percent = nonNegativeNumber(cpu.total);
            if (percent !== null && percent <= 100) snapshot.cpu = percent;
            complete();
        }, complete);
    } catch (error) {
        complete();
    }
}

function readLinuxCpu(snapshot) {
    var values = readTextFile('/proc/stat').split('\n')[0].trim().split(/\s+/).slice(1, 9).map(Number);
    if (
        values.length < 5 ||
        values.some(function (value) {
            return !isFinite(value) || value < 0;
        })
    )
        return;

    var sample = {
        total: values.reduce(function (sum, value) {
            return sum + value;
        }, 0),
        idle: values[3] + values[4]
    };
    if (
        previousCpuSample &&
        sample.total > previousCpuSample.total &&
        sample.idle >= previousCpuSample.idle
    ) {
        var elapsed = sample.total - previousCpuSample.total;
        var idleElapsed = sample.idle - previousCpuSample.idle;
        if (idleElapsed <= elapsed) snapshot.cpu = 100 * (1 - idleElapsed / elapsed);
    }
    previousCpuSample = sample;
}

function collectLinux(snapshot, complete) {
    var release = readTextFile('/etc/os-release').match(/^PRETTY_NAME=(.*)$/m);
    if (release) snapshot.os = release[1].replace(/^"|"$/g, '');

    var processors = readTextFile('/proc/cpuinfo');
    snapshot.cores = (processors.match(/^processor\s*:/gm) || []).length;
    var model = processors.match(/^(?:model name|Hardware)\s*:\s*(.*)$/m);
    if (model) snapshot.cpuModel = model[1];

    var memory = readTextFile('/proc/meminfo');
    var total = memory.match(/^MemTotal:\s*(\d+)/m);
    var available = memory.match(/^MemAvailable:\s*(\d+)/m);
    if (total && available) recordMemory(snapshot, total[1], available[1], 1024);
    readLinuxCpu(snapshot);

    snapshot.manufacturer = readTextFile('/sys/class/dmi/id/sys_vendor');
    snapshot.model = readTextFile('/sys/class/dmi/id/product_name');

    var child = require('child_process').execFile('/bin/sh', [
        'sh',
        '-c',
        'LC_ALL=C uname -m; LC_ALL=C df -Pk -l -x tmpfs -x devtmpfs -x efivarfs -x squashfs'
    ]);
    var output = '';
    child.stdout.on('data', function (chunk) {
        if (output.length < 65536) output += chunk.toString();
    });
    child.stderr.on('data', function () {});
    child.on('exit', function (exitCode) {
        var lines = output.trim().split('\n');
        if (lines[0]) snapshot.architecture = lines[0].trim();
        // df can return an error for one inaccessible mount while still writing
        // valid rows for the others. Do not discard those usable volumes.
        if (lines.length >= 2 && /^Filesystem\s/.test(lines[1])) {
            var disks = [];
            var filesystems = {};
            for (var index = 2; index < lines.length; index++) {
                var columns = lines[index].trim().split(/\s+/);
                if (columns.length < 6 || filesystems[columns[0]]) continue;
                var diskTotal = nonNegativeNumber(columns[1]);
                var diskFree = nonNegativeNumber(columns[3]);
                if (diskTotal > 0 && diskFree !== null && diskFree <= diskTotal) {
                    filesystems[columns[0]] = true;
                    disks.push({
                        name: columns.slice(5).join(' '),
                        total: diskTotal * 1024,
                        free: diskFree * 1024
                    });
                }
            }
            if (disks.length > 0 || exitCode === 0 || exitCode == null) snapshot.disks = disks;
        }
        complete();
    });
    return child;
}

function collect(done) {
    var snapshot = {
        hostname: operatingSystem.hostname(),
        platform: process.platform,
        architecture: process.arch,
        version: String(process.versions.meshAgent || process.versions.compileTime || ''),
        users: getSignedInUsers ? getSignedInUsers() : [],
        cpu: null
    };
    var completed = false;
    var child = null;
    var timeout = setTimeout(function () {
        snapshot.diagnostic = 'Resource collection timed out; unavailable samples were not refreshed.';
        if (child) {
            try {
                child.kill();
            } catch (error) {}
        }
        complete();
    }, 10000);

    function complete() {
        if (completed) return;
        completed = true;
        clearTimeout(timeout);
        done(snapshot);
    }
    function cancel() {
        if (completed) return;
        completed = true;
        clearTimeout(timeout);
        if (child) {
            try {
                child.kill();
            } catch (error) {}
        }
    }

    try {
        try {
            snapshot.uptime = operatingSystem.uptime();
        } catch (error) {}
        readInterfaces(snapshot);
        if (process.platform === 'win32') {
            collectWindows(snapshot, complete);
        } else if (process.platform === 'linux') {
            child = collectLinux(snapshot, complete);
        } else {
            // Unsupported samples remain unknown rather than appearing as idle.
            complete();
        }
    } catch (error) {
        snapshot.diagnostic = 'Resource collection failed: ' + String(error);
        complete();
    }
    return cancel;
}

function refresh() {
    if (collectionBusy || !sendInventory) return;
    collectionBusy = true;
    var generation = collectionGeneration;
    try {
        cancelCollection = collect(function (snapshot) {
            if (generation !== collectionGeneration) return;
            collectionBusy = false;
            cancelCollection = null;
            if (sendInventory) sendInventory({ action: 'meshall-inventory', inventory: snapshot });
        });
    } catch (error) {
        collectionBusy = false;
    }
}

module.exports.start = function (send, getUsers) {
    module.exports.stop();
    sendInventory = send;
    getSignedInUsers = getUsers;
    refresh();
    refreshTimer = setInterval(refresh, 15000);
};

module.exports.stop = function () {
    collectionGeneration++;
    if (refreshTimer) clearInterval(refreshTimer);
    if (cancelCollection) cancelCollection();
    refreshTimer = null;
    cancelCollection = null;
    collectionBusy = false;
    sendInventory = null;
};

module.exports.refresh = refresh;
