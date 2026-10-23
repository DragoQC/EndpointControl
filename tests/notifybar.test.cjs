const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),{EventEmitter}=require('node:events');
const source=fs.readFileSync(require('node:path').join(__dirname,'../components/agent-core/modules_meshcore/meshall-notifybar.js'),'utf8');
function fixture(){
    const sessions=new EventEmitter();sessions.getProcessOwnerName=()=>({tsid:0});const children=[],timers=[];
    function NativeEmitter(){EventEmitter.call(this);for(const key of ['on','emit','removeListener'])this[key]=EventEmitter.prototype[key];this.createEvent=()=>this;this.addMethod=(key,method)=>{this[key]=method;return this;};return this;}
    const original=()=>{};original.MaxWidth=0;original.DefaultPinned=false;
    const context={module:{exports:{}},process:{platform:'win32',pid:1,execPath:'C:\\MeshAllAgent.exe'},Buffer,Date,Math,
        setTimeout:callback=>{const timer={callback};timers.push(timer);return timer;},clearTimeout:timer=>{timer.cancelled=true;},
        require:name=>name==='notifybar-desktop'?original:name==='user-sessions'?sessions:name==='events'?{EventEmitter:NativeEmitter}:name==='child_process'?{execFile:(path,args)=>{const child=new EventEmitter();child.stdout=new EventEmitter();child.stderr=new EventEmitter();child.kill=()=>child.emit('exit',1);child.script=Buffer.from(args[2],'base64').toString();children.push(child);return child;}}:null};
    vm.runInNewContext(source,context);const bar=context.module.exports('Sharing',null,{});let closed=0;bar.on('close',()=>closed++);
    return{bar,sessions,children,timers,closed:()=>closed,flush(){const timer=timers.shift();if(!timer.cancelled)timer.callback();}};
}
test('unexpected helper exit is recreated instead of ending desktop sharing',()=>{const f=fixture();f.children[0].emit('exit',1);assert.equal(f.closed(),0);f.flush();assert.equal(f.children.length,2);assert.equal(f.closed(),0);f.bar.close();});
test('Windows session changes replace only the toolbar helper',()=>{const f=fixture();f.sessions.emit('changed');f.flush();assert.equal(f.children.length,2);assert.equal(f.closed(),0);f.bar.close();});
test('an explicit close-button marker ends sharing and detaches session listeners',()=>{const f=fixture();const marker=f.children[0].script.match(/MESHALL_USER_CLOSED_[0-9]+_[0-9]+/)[0];f.children[0].stdout.emit('data',Buffer.from(marker+'\n'));assert.equal(f.closed(),1);assert.equal(f.sessions.listenerCount('changed'),0);assert.equal(f.bar._closed,true);});
test('programmatic toolbar cleanup never emits a local-user close',()=>{const f=fixture();f.bar.close();assert.equal(f.closed(),0);assert.equal(f.sessions.listenerCount('changed'),0);});

function desktopCloseFixture(count=2){
    const timers=[],immediates=[],delivered=[];
    const streams=Array.from({length:count},()=>Object.assign(new EventEmitter(),{input:true,capture:true,ended:false,
        unpipe(){this.input=false;this.paused=true;},resume(){this.paused=false;if(this.endPending){this.endPending=false;this.emit('end');}},write(message){this.pending=()=>{delivered.push(JSON.parse(message));};},
        end(){this.endCalls=(this.endCalls||0)+1;if(this.ended)return;this.ended=true;this.pending=null;if(this.paused)this.endPending=true;else this.emit('end');}}));
    const kvm={connectionCount:count,tunnels:streams.slice(),_pipedStreams:streams.slice(),unpipe(stream){stream.capture=false;},end(){this.ended=true;this.endCalls=(this.endCalls||0)+1;}};
    for(const stream of streams)stream.on('end',()=>{if(--kvm.connectionCount===0)kvm.end();});
    const context={module:{exports:{}},setImmediate:callback=>immediates.push(callback),setTimeout:callback=>{const timer={callback};timers.push(timer);return timer;},clearTimeout:timer=>{timer.cancelled=true;}};
    vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname,'../components/agent-core/modules_meshcore/meshall-desktop-close.js'),'utf8'),context);
    return{close:()=>context.module.exports(kvm),resume(){while(immediates.length)immediates.shift()();},kvm,streams,timers,delivered};
}
test('local stop detaches input/capture immediately and waits for every viewer acknowledgement before KVM teardown',()=>{
    const f=desktopCloseFixture();f.close();
    assert.ok(f.streams.every(stream=>!stream.input&&!stream.capture&&!stream.ended));
    f.resume();assert.ok(f.streams.every(stream=>!stream.paused&&!stream.input));
    f.streams[0].pending();assert.ok(f.streams.every(stream=>!stream.ended));
    f.streams[1].pending();assert.ok(f.streams.every(stream=>!stream.ended));
    f.streams[0].end();assert.ok(!f.kvm.ended);f.streams[1].end();assert.equal(f.kvm.ended,true);
    assert.ok(f.streams.every(stream=>stream.endCalls===1));assert.equal(f.kvm.connectionCount,0);assert.equal(f.kvm.endCalls,1);
    assert.equal(f.delivered.length,2);assert.ok(f.delivered.every(message=>message.type==='meshall-desktop-ended'&&message.reason==='local-user'));
    assert.equal(f.timers[0].cancelled,true);f.close();assert.equal(f.delivered.length,2);
});
test('a blocked stop notification cannot keep remote input or capture alive',()=>{
    const f=desktopCloseFixture();f.streams[0].write=()=>{throw new Error('Closed transport');};f.close();assert.ok(f.streams.every(stream=>!stream.input&&!stream.capture&&!stream.ended));
    f.timers[0].callback();assert.ok(f.streams.every(stream=>stream.ended));assert.equal(f.kvm.ended,true);
});
test('synchronous native writes still wait for viewer acknowledgement, and no remaining viewers clean up immediately',()=>{
    const f=desktopCloseFixture();for(const stream of f.streams){const write=stream.write;stream.write=function(message){write.call(this,message);this.pending();};}
    f.close();f.resume();assert.equal(f.delivered.length,2);assert.ok(!f.kvm.ended);f.streams.forEach(stream=>stream.end());assert.equal(f.kvm.ended,true);
    const empty=desktopCloseFixture(0);empty.close();assert.equal(empty.kvm.ended,true);assert.equal(empty.timers.length,0);
});
