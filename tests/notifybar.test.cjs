const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs'),{EventEmitter}=require('node:events');
const source=fs.readFileSync(require('node:path').join(__dirname,'../components/agent-core/modules_meshcore/meshall-notifybar.js'),'utf8');
function fixture(){
    const sessions=new EventEmitter();sessions.getProcessOwnerName=()=>({tsid:0});const children=[],timers=[];
    function NativeEmitter(){EventEmitter.call(this);for(const key of ['on','emit','removeListener'])this[key]=EventEmitter.prototype[key];this.createEvent=()=>this;return this;}
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
