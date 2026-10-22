// MeshAll's own RFB client. Negotiates standard mandatory raw encoding and
// CopyRect, desktop resizing, keyboard/pointer input and clipboard messages.
const IP=[58,50,42,34,26,18,10,2,60,52,44,36,28,20,12,4,62,54,46,38,30,22,14,6,64,56,48,40,32,24,16,8,57,49,41,33,25,17,9,1,59,51,43,35,27,19,11,3,61,53,45,37,29,21,13,5,63,55,47,39,31,23,15,7];
const FP=[40,8,48,16,56,24,64,32,39,7,47,15,55,23,63,31,38,6,46,14,54,22,62,30,37,5,45,13,53,21,61,29,36,4,44,12,52,20,60,28,35,3,43,11,51,19,59,27,34,2,42,10,50,18,58,26,33,1,41,9,49,17,57,25];
const E=[32,1,2,3,4,5,4,5,6,7,8,9,8,9,10,11,12,13,12,13,14,15,16,17,16,17,18,19,20,21,20,21,22,23,24,25,24,25,26,27,28,29,28,29,30,31,32,1];
const P=[16,7,20,21,29,12,28,17,1,15,23,26,5,18,31,10,2,8,24,14,32,27,3,9,19,13,30,6,22,11,4,25];
const PC1=[57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35,27,19,11,3,60,52,44,36,63,55,47,39,31,23,15,7,62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,28,20,12,4];
const PC2=[14,17,11,24,1,5,3,28,15,6,21,10,23,19,12,4,26,8,16,7,27,20,13,2,41,52,31,37,47,55,30,40,51,45,33,48,44,49,39,56,34,53,46,42,50,36,29,32];
const S=[
[14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13],
[15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,14,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9],
[10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12],
[7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,1,13,8,9,4,5,11,12,7,2,14],
[2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3],
[12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13],
[4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12],
[13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11]];
const permute=(value,table,bits)=>table.reduce((out,position)=>(out<<1n)|((value>>BigInt(bits-position))&1n),0n);
const fromBytes=bytes=>bytes.reduce((v,b)=>(v<<8n)|BigInt(b),0n);
export function desEncrypt(block,key){
    let k=permute(fromBytes(key),PC1,64),left=k>>28n,right=k&0xfffffffn;const keys=[];
    for(const shift of [1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1]){left=((left<<BigInt(shift))|(left>>BigInt(28-shift)))&0xfffffffn;right=((right<<BigInt(shift))|(right>>BigInt(28-shift)))&0xfffffffn;keys.push(permute((left<<28n)|right,PC2,56));}
    let input=permute(fromBytes(block),IP,64),l=input>>32n,r=input&0xffffffffn;
    for(const key of keys){const expanded=permute(r,E,32)^key;let sub=0n;for(let i=0;i<8;i++){const group=Number((expanded>>BigInt((7-i)*6))&63n),row=((group&32)>>4)|(group&1),column=(group>>1)&15;sub=(sub<<4n)|BigInt(S[i][row*16+column]);}const next=l^permute(sub,P,32);l=r;r=next;}
    let result=permute((r<<32n)|l,FP,64);const bytes=new Uint8Array(8);for(let i=7;i>=0;i--){bytes[i]=Number(result&255n);result>>=8n;}return bytes;
}
function challengeResponse(challenge,password){const encoded=new TextEncoder().encode(password),key=new Uint8Array(8);for(let i=0;i<8;i++){let value=encoded[i]||0,reversed=0;for(let b=0;b<8;b++){reversed=(reversed<<1)|(value&1);value>>=1;}key[i]=reversed;}return new Uint8Array([...desEncrypt(challenge.slice(0,8),key),...desEncrypt(challenge.slice(8),key)]);}
export class MeshAllVnc {
    constructor(canvas,socket,password,state){this.canvas=canvas;this.ctx=canvas.getContext('2d');this.socket=socket;this.password=password;this.report=state;this.queue=new Uint8Array();this.offset=0;this.stage='version';this.rectangles=0;this.rectangle=null;this.mask=0;this.abort=new AbortController();this.bind();}
    push(data){const incoming=new Uint8Array(data),remaining=this.queue.slice(this.offset);this.queue=new Uint8Array(remaining.length+incoming.length);this.queue.set(remaining);this.queue.set(incoming,remaining.length);this.offset=0;try{while(this.step()){} }catch(error){this.report(error.message,false);this.socket.close();}}
    available(n){return this.queue.length-this.offset>=n;}
    take(n){const bytes=this.queue.slice(this.offset,this.offset+n);this.offset+=n;return bytes;}
    u16(bytes,i=0){return (bytes[i]<<8)|bytes[i+1];}
    u32(bytes,i=0){return ((bytes[i]*0x1000000)+(bytes[i+1]<<16)+(bytes[i+2]<<8)+bytes[i+3])>>>0;}
    send(bytes){if(this.socket.readyState===1)this.socket.send(new Uint8Array(bytes));}
    step(){
        if(this.stage==='version'){if(!this.available(12))return false;const version=new TextDecoder().decode(this.take(12));if(!/^RFB 003\./.test(version))throw Error('Invalid VNC protocol greeting.');this.version=Number(version.slice(4,11));this.modern=this.version>=3.8;this.send(new TextEncoder().encode(this.version<3.7?'RFB 003.003\n':'RFB 003.008\n'));this.stage=this.version<3.7?'security33':'security';return true;}
        if(this.stage==='security33'){if(!this.available(4))return false;const security=this.u32(this.take(4));if(security===2)this.stage='challenge';else if(security===1)this.initialize();else throw Error('VNC authentication is unavailable.');return true;}
        if(this.stage==='security'){if(!this.available(1))return false;const count=this.queue[this.offset];if(!count)throw Error('VNC server refused the connection.');if(!this.available(count+1))return false;this.take(1);const choices=this.take(count);this.security=choices.includes(2)?2:choices.includes(1)?1:0;if(!this.security)throw Error('The VNC server requires an unsupported authentication method.');this.send([this.security]);if(this.security===2)this.stage='challenge';else if(this.modern)this.stage='security-result';else this.initialize();return true;}
        if(this.stage==='challenge'){if(!this.available(16))return false;this.send(challengeResponse(this.take(16),this.password));this.password='';this.stage='security-result';return true;}
        if(this.stage==='security-result'){if(!this.available(4))return false;if(this.u32(this.take(4))!==0)throw Error('VNC authentication failed.');this.initialize();return true;}
        if(this.stage==='initialization'){if(!this.available(24))return false;const start=this.queue.slice(this.offset,this.offset+24),nameLength=this.u32(start,20);if(nameLength>1000000)throw Error('Invalid VNC server name.');if(!this.available(24+nameLength))return false;this.take(24);this.take(nameLength);this.canvas.width=this.u16(start);this.canvas.height=this.u16(start,2);this.send([0,0,0,0,32,24,0,1,0,255,0,255,0,255,16,8,0,0,0,0]);this.send([2,0,0,4,0,0,0,0,0,0,0,1,255,255,255,33,255,255,255,32]);this.stage='messages';this.update(false);this.report('Connected',true);return true;}
        if(this.rectangles){
            if(!this.rectangle){if(!this.available(12))return false;const r=this.take(12);this.rectangle={x:this.u16(r),y:this.u16(r,2),w:this.u16(r,4),h:this.u16(r,6),encoding:this.u32(r,8)|0};}
            const r=this.rectangle;if(r.w*r.h>64000000)throw Error('VNC rectangle is too large.');
            if(r.encoding===0){const length=r.w*r.h*4;if(!this.available(length))return false;const pixels=this.take(length),image=this.ctx.createImageData(r.w,r.h);for(let i=0;i<pixels.length;i+=4){image.data[i]=pixels[i+2];image.data[i+1]=pixels[i+1];image.data[i+2]=pixels[i];image.data[i+3]=255;}this.ctx.putImageData(image,r.x,r.y);}
            else if(r.encoding===1){if(!this.available(4))return false;const source=this.take(4);this.ctx.drawImage(this.canvas,this.u16(source),this.u16(source,2),r.w,r.h,r.x,r.y,r.w,r.h);}
            else if(r.encoding===-223){this.canvas.width=r.w;this.canvas.height=r.h;}
            else if(r.encoding===-224)this.rectangles=1;
            else throw Error('Unexpected VNC encoding '+r.encoding);
            this.rectangle=null;this.rectangles--;if(!this.rectangles)this.update(true);return true;
        }
        if(!this.available(1))return false;const type=this.queue[this.offset];
        if(type===0){if(!this.available(4))return false;const message=this.take(4);this.rectangles=this.u16(message,2);if(!this.rectangles)this.update(true);return true;}
        if(type===2){this.take(1);return true;}
        if(type===3){if(!this.available(8))return false;const length=this.u32(this.queue,this.offset+4);if(length>1048576)throw Error('Remote clipboard exceeds the limit.');if(!this.available(8+length))return false;this.take(8);this.clipboard=new TextDecoder().decode(this.take(length));return true;}
        throw Error('Unexpected VNC message '+type);
    }
    initialize(){this.send([1]);this.stage='initialization';}
    update(incremental){const w=this.canvas.width,h=this.canvas.height;this.send([3,incremental?1:0,0,0,0,0,w>>8,w&255,h>>8,h&255]);}
    key(key,down){this.send([4,down?1:0,0,0,(key>>>24)&255,(key>>>16)&255,(key>>>8)&255,key&255]);}
    pointer(event){const r=this.canvas.getBoundingClientRect(),x=Math.max(0,Math.min(this.canvas.width-1,Math.floor((event.clientX-r.left)*this.canvas.width/r.width))),y=Math.max(0,Math.min(this.canvas.height-1,Math.floor((event.clientY-r.top)*this.canvas.height/r.height)));this.send([5,this.mask,x>>8,x&255,y>>8,y&255]);}
    bind(){const opts={signal:this.abort.signal};this.canvas.tabIndex=0;this.canvas.addEventListener('pointerdown',e=>{e.preventDefault();this.canvas.focus();this.canvas.setPointerCapture(e.pointerId);this.mask|=[1,2,4][e.button]||1;this.pointer(e);},opts);this.canvas.addEventListener('pointerup',e=>{e.preventDefault();this.mask&=~([1,2,4][e.button]||1);this.pointer(e);},opts);this.canvas.addEventListener('pointermove',e=>this.pointer(e),opts);this.canvas.addEventListener('contextmenu',e=>e.preventDefault(),opts);this.canvas.addEventListener('wheel',e=>{e.preventDefault();const mask=this.mask;this.mask|=e.deltaY>0?16:8;this.pointer(e);this.mask=mask;this.pointer(e);},{...opts,passive:false});for(const type of ['keydown','keyup'])this.canvas.addEventListener(type,e=>{e.preventDefault();const specials={Backspace:0xff08,Tab:0xff09,Enter:0xff0d,Escape:0xff1b,Home:0xff50,ArrowLeft:0xff51,ArrowUp:0xff52,ArrowRight:0xff53,ArrowDown:0xff54,PageUp:0xff55,PageDown:0xff56,End:0xff57,Insert:0xff63,Delete:0xffff,Shift:0xffe1,Control:0xffe3,Alt:0xffe9,Meta:0xffeb};let code=specials[e.key];if(!code&&/^F\d+$/.test(e.key))code=0xffbd+Number(e.key.slice(1));if(!code&&Array.from(e.key).length===1){code=e.key.codePointAt(0);if(code>255)code|=0x1000000;}if(code)this.key(code,type==='keydown');},opts);}
    dispose(){this.abort.abort();this.socket.close();this.password='';}
}
