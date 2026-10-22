// MeshAll terminal renderer. Original browser-side ANSI/VT handling.
export class MeshAllTerminal {
    constructor(host, send, resize) {
        this.host=host;this.send=send;this.onResize=resize;this.columns=100;this.rows=30;this.x=0;this.y=0;
        this.top=0;this.bottom=29;this.saved=[0,0];this.state='';this.sequence='';this.cursor=true;this.wrap=true;
        this.fg='#d6e6e0';this.bg='#12232d';this.bold=false;this.underline=false;this.inverse=false;
        this.defaults=[this.fg,this.bg];this.lines=[];this.alternate=null;this.decoder=new TextDecoder();this.dirty=true;
        host.replaceChildren();host.classList.add('meshall-terminal');host.tabIndex=0;
        this.screen=document.createElement('div');this.screen.className='terminal-screen';this.screen.setAttribute('role','log');this.screen.setAttribute('aria-label','Remote terminal output');
        this.input=document.createElement('textarea');this.input.className='terminal-input';this.input.setAttribute('aria-label','Remote terminal input');this.input.autocomplete='off';this.input.spellcheck=false;
        host.append(this.screen,this.input);this.abort=new AbortController();const opts={signal:this.abort.signal};
        host.addEventListener('pointerdown',()=>this.focus(),opts);this.input.addEventListener('keydown',e=>this.key(e),opts);
        this.input.addEventListener('input',()=>{if(this.input.value){this.send(this.input.value);this.input.value='';}},opts);
        this.input.addEventListener('paste',e=>{e.preventDefault();this.send(e.clipboardData.getData('text/plain'));},opts);
        this.fit();this.observer=new ResizeObserver(()=>this.fit());this.observer.observe(host);
        this.timer=setInterval(()=>{if(this.dirty){this.paint();this.dirty=false;}},30);
    }
    blank(){return Array.from({length:this.columns},()=>({c:' ',f:this.defaults[0],b:this.defaults[1],bold:false,u:false}));}
    fit(){
        const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d');ctx.font='13px Consolas, monospace';const width=ctx.measureText('M').width||8;
        const columns=Math.max(20,Math.min(300,Math.floor((this.host.clientWidth-24)/width))),rows=Math.max(5,Math.min(150,Math.floor((this.host.clientHeight-24)/18)));
        if(columns===this.columns&&rows===this.rows&&this.lines.length)return;
        this.columns=columns;this.rows=rows;this.top=0;this.bottom=rows-1;
        this.lines=this.lines.slice(0,rows);while(this.lines.length<rows)this.lines.push(this.blank());
        for(const line of this.lines){line.length=Math.min(line.length,columns);while(line.length<columns)line.push({c:' ',f:this.defaults[0],b:this.defaults[1]});}
        this.x=Math.min(this.x,columns-1);this.y=Math.min(this.y,rows-1);this.dirty=true;this.onResize?.(columns,rows);
    }
    focus(){this.input.focus({preventScroll:true});}
    write(bytes){const text=typeof bytes==='string'?bytes:this.decoder.decode(bytes,{stream:true});for(const c of text)this.character(c);this.dirty=true;}
    character(c){
        if(this.state==='osc'){if(c==='\x07'){this.state='';}else if(c==='\x1b')this.state='osc-end';return;}
        if(this.state==='osc-end'){this.state=c==='\\'?'':'osc';return;}
        if(this.state==='charset'){this.state='';return;}
        if(this.state==='escape'){
            this.state='';if(c==='['){this.state='csi';this.sequence='';}else if(c===']')this.state='osc';
            else if(c==='7')this.saved=[this.x,this.y];else if(c==='8')[this.x,this.y]=this.saved;
            else if(c==='D')this.lineFeed();else if(c==='E'){this.x=0;this.lineFeed();}else if(c==='M'){if(this.y===this.top)this.scroll(-1);else this.y=Math.max(0,this.y-1);}
            else if(c==='('||c===')')this.state='charset';else if(c==='c')this.reset();return;
        }
        if(this.state==='csi'){if(c>='@'&&c<='~'){this.csi(c,this.sequence);this.state='';}else if(this.sequence.length<128)this.sequence+=c;else this.state='';return;}
        if(c==='\x1b'){this.state='escape';return;}if(c==='\r'){this.x=0;return;}if(c==='\n'||c==='\x0b'||c==='\x0c'){this.lineFeed();return;}
        if(c==='\b'){this.x=Math.max(0,this.x-1);return;}if(c==='\t'){this.x=Math.min(this.columns-1,(Math.floor(this.x/8)+1)*8);return;}if(c<' '||c==='\x7f')return;
        if(/\p{Mark}/u.test(c)&&this.x>0){this.lines[this.y][this.x-1].c+=c;return;}
        const code=c.codePointAt(0),wide=code>=0x1100&&(code<=0x115f||code>=0x2e80&&code<=0xa4cf||code>=0xac00&&code<=0xd7a3||code>=0xf900&&code<=0xfaff||code>=0xff00&&code<=0xff60||code>=0x1f300)?2:1;
        if(this.x>=this.columns){if(this.wrap){this.x=0;this.lineFeed();}else this.x=this.columns-1;}
        this.lines[this.y][this.x]={c,f:this.inverse?this.bg:this.fg,b:this.inverse?this.fg:this.bg,bold:this.bold,u:this.underline};
        if(wide===2&&this.x+1<this.columns)this.lines[this.y][this.x+1]={c:'',f:this.fg,b:this.bg};this.x+=wide;
    }
    lineFeed(){if(this.y===this.bottom)this.scroll(1);else this.y=Math.min(this.rows-1,this.y+1);}
    scroll(n){for(let i=0;i<Math.abs(n);i++)if(n>0){this.lines.splice(this.top,1);this.lines.splice(this.bottom,0,this.blank());}else{this.lines.splice(this.bottom,1);this.lines.splice(this.top,0,this.blank());}}
    erase(y,start,end){for(let x=Math.max(0,start);x<Math.min(this.columns,end);x++)this.lines[y][x]={c:' ',f:this.fg,b:this.bg};}
    reset(){this.x=this.y=0;this.fg=this.defaults[0];this.bg=this.defaults[1];this.bold=this.underline=this.inverse=false;this.lines=Array.from({length:this.rows},()=>this.blank());this.top=0;this.bottom=this.rows-1;}
    csi(final,sequence){
        const privateMode=sequence.startsWith('?');if(privateMode)sequence=sequence.slice(1);
        const p=sequence.split(';').map(s=>Number(s)||0),n=p[0]||1;
        switch(final){
            case 'A':this.y=Math.max(this.top,this.y-n);break;case 'B':this.y=Math.min(this.bottom,this.y+n);break;
            case 'C':this.x=Math.min(this.columns-1,this.x+n);break;case 'D':this.x=Math.max(0,this.x-n);break;
            case 'E':this.y=Math.min(this.rows-1,this.y+n);this.x=0;break;case 'F':this.y=Math.max(0,this.y-n);this.x=0;break;
            case 'G':case '`':this.x=Math.min(this.columns-1,n-1);break;case 'd':this.y=Math.min(this.rows-1,n-1);break;
            case 'H':case 'f':this.y=Math.min(this.rows-1,(p[0]||1)-1);this.x=Math.min(this.columns-1,(p[1]||1)-1);break;
            case 'J':if(p[0]===2||p[0]===3){for(let y=0;y<this.rows;y++)this.erase(y,0,this.columns);}else if(p[0]===1){for(let y=0;y<this.y;y++)this.erase(y,0,this.columns);this.erase(this.y,0,this.x+1);}else{this.erase(this.y,this.x,this.columns);for(let y=this.y+1;y<this.rows;y++)this.erase(y,0,this.columns);}break;
            case 'K':this.erase(this.y,p[0]===0?this.x:0,p[0]===1?this.x+1:this.columns);break;
            case 'm':this.sgr(p);break;case 's':this.saved=[this.x,this.y];break;case 'u':[this.x,this.y]=this.saved;break;
            case 'r':this.top=Math.max(0,(p[0]||1)-1);this.bottom=Math.min(this.rows-1,(p[1]||this.rows)-1);this.x=0;this.y=this.top;break;
            case 'S':this.scroll(n);break;case 'T':this.scroll(-n);break;
            case 'L':for(let i=0;i<n;i++){this.lines.splice(this.y,0,this.blank());this.lines.splice(this.bottom+1,1);}break;
            case 'M':for(let i=0;i<n;i++){this.lines.splice(this.y,1);this.lines.splice(this.bottom,0,this.blank());}break;
            case '@':this.lines[this.y].splice(this.x,0,...this.blank().slice(0,n));this.lines[this.y].length=this.columns;break;
            case 'P':this.lines[this.y].splice(this.x,n);this.lines[this.y].push(...this.blank().slice(0,n));this.lines[this.y].length=this.columns;break;
            case 'X':this.erase(this.y,this.x,this.x+n);break;
            case 'n':if(p[0]===6)this.send(`\x1b[${this.y+1};${Math.min(this.x,this.columns-1)+1}R`);else if(p[0]===5)this.send('\x1b[0n');break;
            case 'c':this.send('\x1b[?1;2c');break;
            case 'h':case 'l':if(privateMode)for(const mode of p){const on=final==='h';if(mode===25)this.cursor=on;if(mode===7)this.wrap=on;if(mode===1049){if(on&&!this.alternate){this.alternate={lines:this.lines,x:this.x,y:this.y};this.lines=Array.from({length:this.rows},()=>this.blank());this.x=this.y=0;}else if(!on&&this.alternate){Object.assign(this,this.alternate);this.alternate=null;}}}break;
        }
    }
    sgr(p){
        const colors=['#17202b','#e26369','#70c79a','#d7be71','#78a7e4','#ba91db','#78c5cc','#d6e6e0','#6f8094','#ff858a','#94e3b7','#ffe094','#a4caff','#ddb2ff','#9fe5ed','#ffffff'];
        const color256=n=>n<16?colors[n]:n<232?`rgb(${[Math.floor((n-16)/36),Math.floor((n-16)%36/6),(n-16)%6].map(c=>c?55+c*40:0).join(',')})`:`rgb(${Array(3).fill(8+(n-232)*10).join(',')})`;
        for(let i=0;i<p.length;i++){const n=p[i];if(n===0){this.fg=this.defaults[0];this.bg=this.defaults[1];this.bold=this.underline=this.inverse=false;}else if(n===1)this.bold=true;else if(n===22)this.bold=false;else if(n===4)this.underline=true;else if(n===24)this.underline=false;else if(n===7)this.inverse=true;else if(n===27)this.inverse=false;else if(n>=30&&n<=37)this.fg=colors[n-30];else if(n>=40&&n<=47)this.bg=colors[n-40];else if(n>=90&&n<=97)this.fg=colors[n-90+8];else if(n>=100&&n<=107)this.bg=colors[n-100+8];else if(n===39)this.fg=this.defaults[0];else if(n===49)this.bg=this.defaults[1];else if(n===38||n===48){let color;if(p[i+1]===5){color=color256(Math.min(255,p[i+2]||0));i+=2;}else if(p[i+1]===2){color=`rgb(${p.slice(i+2,i+5).map(v=>Math.min(255,v)).join(',')})`;i+=4;}if(color){if(n===38)this.fg=color;else this.bg=color;}}}
    }
    key(e){
        if(e.ctrlKey&&e.shiftKey&&(e.key==='C'||e.key==='V'))return;
        let data={Enter:'\r',Backspace:'\x7f',Tab:e.shiftKey?'\x1b[Z':'\t',Escape:'\x1b',ArrowUp:'\x1b[A',ArrowDown:'\x1b[B',ArrowRight:'\x1b[C',ArrowLeft:'\x1b[D',Home:'\x1b[H',End:'\x1b[F',Insert:'\x1b[2~',Delete:'\x1b[3~',PageUp:'\x1b[5~',PageDown:'\x1b[6~',F1:'\x1bOP',F2:'\x1bOQ',F3:'\x1bOR',F4:'\x1bOS',F5:'\x1b[15~',F6:'\x1b[17~',F7:'\x1b[18~',F8:'\x1b[19~',F9:'\x1b[20~',F10:'\x1b[21~',F11:'\x1b[23~',F12:'\x1b[24~'}[e.key];
        if(e.ctrlKey&&e.key.length===1){const code=e.key.toUpperCase().charCodeAt(0);if(code>=64&&code<=95)data=String.fromCharCode(code-64);}
        else if(e.altKey&&e.key.length===1)data='\x1b'+e.key;
        if(data!==undefined){e.preventDefault();this.send(data);}
    }
    paint(){
        const fragment=document.createDocumentFragment();
        for(let y=0;y<this.rows;y++){
            const row=document.createElement('div');row.className='terminal-row';let run=null,last='';
            for(let x=0;x<this.columns;x++){const c=this.lines[y]?.[x]||{c:' ',f:this.defaults[0],b:this.defaults[1]},cursor=this.cursor&&x===Math.min(this.x,this.columns-1)&&y===this.y;const style=[c.f,c.b,c.bold,c.u,cursor].join('|');if(style!==last){run=document.createElement('span');run.style.color=cursor?this.defaults[1]:c.f;run.style.backgroundColor=cursor?this.defaults[0]:c.b;run.style.fontWeight=c.bold?'700':'400';run.style.textDecoration=c.u?'underline':'none';row.append(run);last=style;}run.append(document.createTextNode(c.c));}
            fragment.append(row);
        }
        this.screen.replaceChildren(fragment);
    }
    dispose(){clearInterval(this.timer);this.observer.disconnect();this.abort.abort();this.host.replaceChildren();}
}
