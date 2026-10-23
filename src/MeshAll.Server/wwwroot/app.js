(() => {
    function restoreTheme(){let theme='light';try{theme=localStorage.getItem('meshall-theme')==='dark'?'dark':'light';}catch{}document.documentElement.dataset.theme=theme;}
    restoreTheme();
    const loaded=new Map();
    const script=src=>{if(loaded.has(src))return loaded.get(src);const task=new Promise((resolve,reject)=>{const s=document.createElement('script');s.src=src;s.onload=resolve;s.onerror=()=>reject(Error('Unable to load browser renderer. Run npm run build.'));document.head.append(s);});loaded.set(src,task);return task;};
    const css=href=>{if(!document.querySelector(`link[href="${href}"]`)){const link=document.createElement('link');link.rel='stylesheet';link.href=href;document.head.append(link);}};
    const url=path=>location.protocol.replace('http','ws')+'//'+location.host+path;
    let active=null;
    function observeSize(session,elements,fit){
        const update=()=>{if(active===session)fit();};session.sizeObserver=new ResizeObserver(update);
        for(const element of elements)session.sizeObserver.observe(element);
        window.addEventListener('resize',update,{signal:session.abort.signal});document.addEventListener('fullscreenchange',update,{signal:session.abort.signal});requestAnimationFrame(update);
    }
    function fitCanvas(canvas){
        const stage=canvas.parentElement;if(!stage||!canvas.width||!canvas.height)return;
        const scale=Math.min(stage.clientWidth/canvas.width,stage.clientHeight/canvas.height);if(!(scale>0))return;
        canvas.style.width=Math.floor(canvas.width*scale)+'px';canvas.style.height=Math.floor(canvas.height*scale)+'px';
    }
    function status(session,text,connected){if(active===session)session.dotnet.invokeMethodAsync('StateAsync',text,connected).catch(()=>{});}
    function desktopInput(canvas,socket,session){
        const options={signal:session.abort.signal};let move=0;
        const send=value=>{if(session.control&&socket.readyState===1)socket.send(JSON.stringify(value));};
        const pointer=(e,type)=>{if(!session.control)return;e.preventDefault();const r=canvas.getBoundingClientRect();send({type,x:Math.max(0,Math.min(1,(e.clientX-r.left)/r.width)),y:Math.max(0,Math.min(1,(e.clientY-r.top)/r.height)),button:e.button});};
        canvas.addEventListener('pointerdown',e=>{canvas.focus();if(session.control)canvas.setPointerCapture(e.pointerId);pointer(e,'down');},options);
        canvas.addEventListener('pointerup',e=>pointer(e,'up'),options);
        canvas.addEventListener('pointermove',e=>{if(performance.now()-move>40){move=performance.now();pointer(e,'move');}},options);
        canvas.addEventListener('contextmenu',e=>{if(session.control)e.preventDefault();},options);
        canvas.addEventListener('wheel',e=>{if(session.control){e.preventDefault();send({type:'wheel',delta:e.deltaY>0?-120:120});}},{...options,passive:false});
        for(const type of ['keydown','keyup'])canvas.addEventListener(type,e=>{if(session.control){e.preventDefault();send({type,key:e.keyCode});}},options);
        canvas.addEventListener('blur',()=>send({type:'release'}),options);
    }
    window.meshAll={
        restoreTheme,
        toggleTheme(){const next=document.documentElement.dataset.theme==='dark'?'light':'dark';try{localStorage.setItem('meshall-theme',next);}catch{}document.documentElement.dataset.theme=next;},
        remote:{
            async connect(options,dotnet){
                this.disconnect();const session={dotnet,abort:new AbortController(),control:false,owner:options.owner};active=session;
                const canvas=document.getElementById('remote-canvas'),terminal=document.getElementById('remote-terminal'),surface=document.getElementById('remote-vnc');
                canvas.style.display=options.tool==='desktop'?'block':'none';terminal.style.display=['terminal','powershell'].includes(options.tool)?'block':'none';surface.style.display=['rdp','vnc','ssh','amt'].includes(options.tool)?'flex':'none';
                if(options.tool==='desktop')observeSize(session,[canvas.parentElement],()=>fitCanvas(canvas));
                try {
                    if(options.tool==='desktop'&&options.native){
                        for(const file of ['common-0.0.1.js','agent-desktop-0.0.2.js','agent-redir-ws-0.1.1.js'])await script('/vendor/native-viewer/scripts/'+file);
                        if(active!==session)return;
                        window.urlargs={};const desktop=CreateAgentRemoteDesktop(canvas);desktop.stopInput=true;desktop.onScreenSizeChange=()=>fitCanvas(canvas);session.nativeDesktop=desktop;
                        const redirect=CreateAgentRedirect(null,desktop,null,null,null,'/');session.redirect=redirect;
                        redirect.Start=()=>{redirect.socket=new WebSocket(url(`/api/remote/${options.id}/desktop`));redirect.socket.binaryType='arraybuffer';redirect.socket.onopen=redirect.xxOnSocketConnected;redirect.socket.onmessage=redirect.xxOnMessage;redirect.socket.onclose=redirect.xxOnSocketClosed;redirect.xxStateChange(1);};
                        redirect.onStateChanged=(_,next)=>status(session,next===3?'Connected':next===0?'Disconnected':'Connecting…',next!==0);
                        redirect.onConsoleMessageChange=()=>{if(redirect.consoleMessage)status(session,redirect.consoleMessage,true);else if(redirect.State===3)status(session,'Connected',true);};redirect.Start();
                    } else if(options.tool==='desktop'){
                        const socket=new WebSocket(url(`/api/remote/${options.id}/desktop`));session.socket=socket;desktopInput(canvas,socket,session);
                        let painting=Promise.resolve();
                        socket.onmessage=e=>{const message=JSON.parse(e.data);if(message.type==='status')status(session,message.message,true);else if(message.type==='error'){status(session,message.message,false);socket.close();}else if(message.type==='frame'){painting=painting.then(()=>new Promise(resolve=>{const image=new Image();image.onload=()=>{if(active===session){if(canvas.width!==message.width||canvas.height!==message.height){canvas.width=message.width;canvas.height=message.height;}canvas.getContext('2d').drawImage(image,0,0);fitCanvas(canvas);status(session,'Connected',true);}resolve();};image.onerror=resolve;image.src='data:image/jpeg;base64,'+message.base64;}));}};
                        socket.onclose=()=>status(session,'Disconnected',false);socket.onerror=()=>status(session,'Desktop connection failed.',false);
                    } else if(['terminal','powershell'].includes(options.tool)){
                        await script('/vendor/xterm/xterm.js');css('/vendor/xterm/xterm.css');
                        if(active!==session)return;
                        const term=new Terminal({cursorBlink:true,screenReaderMode:true,fontSize:13,fontFamily:'Consolas, monospace',theme:{background:'#12232d',foreground:'#d6e6e0'}});term.open(terminal);session.terminal=term;
                        const socket=new WebSocket(url(`/api/network/${options.id}/${options.tool}`));socket.binaryType='arraybuffer';session.socket=socket;let ready=false;
                        const fit=()=>{term.resize(Math.max(20,Math.floor(terminal.clientWidth/8)),Math.max(5,Math.floor(terminal.clientHeight/17)));if(socket.readyState===1)socket.send(JSON.stringify({type:'resize',columns:term.cols,rows:term.rows}));};observeSize(session,[terminal],fit);
                        socket.onmessage=e=>{if(typeof e.data==='string'&&e.data.startsWith('{')){const m=JSON.parse(e.data);if(m.type==='status')status(session,m.message,true);return;}if(!ready){ready=true;status(session,'Connected',true);term.focus();fit();}term.write(new Uint8Array(e.data));};
                        term.onData(data=>{if(ready&&socket.readyState===1)socket.send(new TextEncoder().encode(data));});socket.onclose=e=>status(session,e.reason||'Disconnected',false);socket.onerror=()=>status(session,'Terminal connection failed.',false);
                    } else if(options.tool==='vnc'){
                        const {default:RFB}=await import('/vendor/novnc/core/rfb.js');
                        if(active!==session)return;
                        const client=new RFB(surface,url(`/api/network/${options.id}/vnc?port=${options.port}&direct=${options.direct?1:0}`),{credentials:{username:options.username,password:options.password}});session.vnc=client;client.scaleViewport=true;client.resizeSession=true;
                        client.addEventListener('connect',()=>status(session,'Connected',true));client.addEventListener('disconnect',e=>status(session,e.detail.clean?'Disconnected':'VNC connection failed. Check the service and credentials.',false));client.addEventListener('credentialsrequired',()=>client.sendCredentials({username:options.username,password:options.password}));
                    } else if(options.tool==='rdp'||options.tool==='ssh'){
                        const {default:Guacamole}=await import('/vendor/guacamole/dist/esm/guacamole-common.js');
                        if(active!==session)return;
                        const tunnel=new Guacamole.Tunnel(),parser=new Guacamole.Parser(),decoder=new TextDecoder();let socket;
                        parser.oninstruction=(opcode,args)=>tunnel.oninstruction?.(opcode,args);
                        tunnel.sendMessage=(...parts)=>{if(socket?.readyState===1)socket.send(parts.map(v=>String(v).length+'.'+v).join(',')+';');};
                        tunnel.connect=data=>{socket=new WebSocket(url(`/api/network/${options.id}/${options.tool}?port=${options.port}&direct=${options.direct?1:0}`));socket.binaryType='arraybuffer';socket.onopen=()=>{socket.send(data);tunnel.setUUID(crypto.randomUUID());tunnel.setState(Guacamole.Tunnel.State.OPEN);};socket.onmessage=e=>parser.receive(typeof e.data==='string'?e.data:decoder.decode(new Uint8Array(e.data),{stream:true}));socket.onerror=()=>tunnel.onerror?.(new Guacamole.Status(0x0200,'Remote protocol connection failed.'));socket.onclose=e=>{tunnel.setState(Guacamole.Tunnel.State.CLOSED);status(session,e.reason||'Disconnected',false);};};
                        tunnel.disconnect=()=>{socket?.close();tunnel.setState(Guacamole.Tunnel.State.CLOSED);};
                        const client=new Guacamole.Client(tunnel);session.guac=client;const display=client.getDisplay().getElement();
                        // Guacamole paints canvases at negative z-index within each layer.
                        // Keep those layers above the remote surface's background.
                        display.style.isolation='isolate';surface.replaceChildren(display);
                        const fit=()=>{const screen=client.getDisplay();if(screen.getWidth()>0&&screen.getHeight()>0)screen.scale(Math.min(surface.clientWidth/screen.getWidth(),surface.clientHeight/screen.getHeight()));};client.getDisplay().onresize=fit;observeSize(session,[surface],fit);
                        const mouse=new Guacamole.Mouse(client.getDisplay().getElement());mouse.onmousedown=mouse.onmouseup=mouse.onmousemove=s=>client.sendMouseState(s,true);
                        const keyboard=new Guacamole.Keyboard(surface);surface.tabIndex=0;surface.addEventListener('pointerdown',()=>surface.focus(),{signal:session.abort.signal});keyboard.onkeydown=key=>{client.sendKeyEvent(1,key);return false;};keyboard.onkeyup=key=>client.sendKeyEvent(0,key);session.keyboard=keyboard;
                        client.onstatechange=state=>{if(state===3)status(session,'Connected',true);else if(state===5)status(session,'Disconnected',false);};client.onerror=e=>status(session,e.message||'Remote login failed.',false);
                        // Credentials are sent through the authenticated same-origin WebSocket,
                        // never a query string or the audit log.
                        client.connect(JSON.stringify({username:options.username,password:options.password,domain:options.domain,privateKey:options.privateKey,width:surface.clientWidth,height:Math.max(240,surface.clientHeight),dpi:96}));
                    } else throw Error('This remote transport is still being implemented.');
                }catch(error){if(active===session)this.disconnect();throw error;}
            },
            control(enabled){if(active){active.control=enabled;const desktop=active.nativeDesktop;if(desktop){desktop.stopInput=!enabled;if(enabled){desktop.GrabMouseInput();desktop.GrabKeyInput();}else{desktop.UnGrabMouseInput();desktop.UnGrabKeyInput();}}}},
            cad(){if(!active?.control)return;if(active.nativeDesktop){active.nativeDesktop.SendCtrlAltDelMsg();return;}if(active.socket?.readyState!==1)return;for(const key of [17,18,46])active.socket.send(JSON.stringify({type:'keydown',key}));for(const key of [46,18,17])active.socket.send(JSON.stringify({type:'keyup',key}));},
            disconnect(owner){if(owner&&active?.owner!==owner)return;const s=active;active=null;if(!s)return;s.abort.abort();s.sizeObserver?.disconnect();s.redirect?.Stop();s.socket?.close();s.terminal?.dispose();s.vnc?.disconnect();s.guac?.disconnect();s.keyboard?.reset();}
        }
    };
    document.addEventListener('click',event=>{
        if(!(event.target instanceof Element))return;
        if(event.target.closest('[data-theme-toggle]'))window.meshAll.toggleTheme();
        const button=event.target.closest('[data-remote-fullscreen]');if(button){const root=button.closest('[data-remote-workspace]');const task=document.fullscreenElement?document.exitFullscreen():root?.requestFullscreen();task?.catch(()=>{});}
    });
    window.addEventListener('storage',event=>{if(event.key==='meshall-theme')restoreTheme();});
})();
