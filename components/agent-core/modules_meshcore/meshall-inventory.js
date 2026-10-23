/* MeshAll inventory collector. Copyright DragoQC and contributors. MIT license. */
var fs = require('fs'), os = require('os');
var timer = null, busy = false, previousCpu = null, sender = null, users = null;
function read(path) { try { return fs.readFileSync(path).toString().trim(); } catch (e) { return ''; } }
function finite(value) { if(value==null || value==='')return null;value = Number(value); return isFinite(value) && value >= 0 ? value : null; }
function collect(done) {
    var result = { hostname: os.hostname(), platform: process.platform, architecture: process.arch, version: String(process.versions.meshAgent || process.versions.compileTime || ''), users: users ? users() : [], interfaces: [], disks: [], cpu: null };
    try { result.uptime = os.uptime(); } catch (e) { }
    try {
        var nics = os.networkInterfaces();
        Object.keys(nics).forEach(function (name) {
            nics[name].forEach(function (item) {
                result.interfaces.push({ name: name, address: item.address, family: String(item.family), mac: item.mac || '', internal: !!item.internal });
            });
        });
    } catch (e) { }
    if (process.platform == 'win32') {
        try {
            var wmi = require('win-wmi-fixed');
            var system = wmi.query('ROOT\\CIMV2', 'SELECT Manufacturer,Model,NumberOfLogicalProcessors,UserName FROM Win32_ComputerSystem', ['Manufacturer','Model','NumberOfLogicalProcessors','UserName'])[0];
            var operating = wmi.query('ROOT\\CIMV2', 'SELECT Caption,Version,BuildNumber,OSArchitecture,TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem', ['Caption','Version','BuildNumber','OSArchitecture','TotalVisibleMemorySize','FreePhysicalMemory'])[0];
            var processors = wmi.query('ROOT\\CIMV2', 'SELECT Name,Architecture FROM Win32_Processor', ['Name','Architecture']);
            if (system) { result.manufacturer=system.Manufacturer;result.model=system.Model;result.cores=finite(system.NumberOfLogicalProcessors);if(system.UserName)result.users=[system.UserName]; }
            if (operating) {
                result.os=operating.Caption+' '+operating.Version+' (build '+operating.BuildNumber+')';
                result.memoryTotal=finite(operating.TotalVisibleMemorySize)*1024;result.memoryUsed=result.memoryTotal-finite(operating.FreePhysicalMemory)*1024;
                if(operating.OSArchitecture)result.architecture=String(operating.OSArchitecture).indexOf('64')>=0?'x64':'x86';
            }
            if(processors && processors.length) { result.cpuModel=processors.map(function(p){return p.Name;}).join(' / ');if(Number(processors[0].Architecture)==12)result.architecture='arm64'; }
            wmi.query('ROOT\\CIMV2', 'SELECT DeviceID,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3', ['DeviceID','Size','FreeSpace']).forEach(function(d){if(finite(d.Size)>0)result.disks.push({name:d.DeviceID,total:finite(d.Size),free:finite(d.FreeSpace)});});
        } catch (e) { result.diagnostic='Windows inventory query failed: '+String(e); }
        try {
            var mem=require('sysinfo').memUtilization();result.memoryTotal=finite(mem.MemTotal);result.memoryUsed=result.memoryTotal-finite(mem.MemFree);
            require('sysinfo').cpuUtilization().then(function(cpu){result.cpu=finite(cpu.total);done(result);},function(){done(result);});return;
        } catch(e) { done(result);return; }
    }
    if(process.platform=='linux') {
        var release=read('/etc/os-release').match(/^PRETTY_NAME=(.*)$/m);if(release)result.os=release[1].replace(/^"|"$/g,'');
        var cpu=read('/proc/cpuinfo');result.cores=(cpu.match(/^processor\s*:/gm)||[]).length;
        var model=cpu.match(/^(?:model name|Hardware)\s*:\s*(.*)$/m);if(model)result.cpuModel=model[1];
        var mem=read('/proc/meminfo'),total=mem.match(/^MemTotal:\s*(\d+)/m),available=mem.match(/^MemAvailable:\s*(\d+)/m);
        if(total && available){result.memoryTotal=Number(total[1])*1024;result.memoryUsed=result.memoryTotal-Number(available[1])*1024;}
        var stat=read('/proc/stat').split('\n')[0].trim().split(/\s+/).slice(1).map(Number);
        var sample={total:stat.slice(0,8).reduce(function(a,b){return a+b;},0),idle:stat[3]+stat[4]};
        if(previousCpu && sample.total>previousCpu.total)result.cpu=Math.max(0,Math.min(100,100*(1-(sample.idle-previousCpu.idle)/(sample.total-previousCpu.total))));
        previousCpu=sample;
        result.manufacturer=read('/sys/class/dmi/id/sys_vendor');result.model=read('/sys/class/dmi/id/product_name');
        try {
            var child=require('child_process').execFile('/bin/sh',['sh','-c','LC_ALL=C uname -m; LC_ALL=C df -Pk -l -x tmpfs -x devtmpfs -x efivarfs -x squashfs']);
            child._inventory=result;child._output='';child._done=done;
            child.stdout.on('data',function(chunk){child._output+=chunk.toString();});
            child.on('exit',function(){
                var lines=this._output.trim().split('\n');if(lines[0])this._inventory.architecture=lines[0].trim();
                for(var i=2;i<lines.length;i++){var parts=lines[i].trim().split(/\s+/);if(parts.length>=6 && finite(parts[1])>0)this._inventory.disks.push({name:parts.slice(5).join(' '),total:Number(parts[1])*1024,free:Number(parts[3])*1024});}
                this._done(this._inventory);
            });return;
        } catch(e) { done(result);return; }
    }
    // Other native platforms retain their last known fields until reported.
    done(result);
}
function refresh() {
    if(busy || !sender)return;
    busy=true;
    try { collect(function(result){busy=false;if(sender)sender({action:'meshall-inventory',inventory:result});}); }
    catch(e){busy=false;}
}
module.exports.start=function(send,getUsers){module.exports.stop();sender=send;users=getUsers;refresh();timer=setInterval(refresh,30000);};
module.exports.stop=function(){if(timer)clearInterval(timer);timer=null;sender=null;};
module.exports.refresh=refresh;
