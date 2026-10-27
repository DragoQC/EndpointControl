/* EndpointControl native approval dialog. Copyright DragoQC and contributors. MIT. */
// Dispatch through the proven native process API. Anonymous stdout pipes avoid
// a discoverable named IPC endpoint for security-sensitive approval decisions.
var Promise = require('promise');

function create(title, caption, seconds, layout, uid) {
    var result = new Promise(Promise.defaultInit);
    var settled = false;
    var closed = false;
    var marker = 'MESHALL_SCREEN_DECISION_';
    var logo = '';
    try {
        logo = require('meshall-consent-logo');
    } catch (error) {}
    // Duktape does not retain JavaScript source for Function.toString(). Build
    // this small, self-contained child explicitly, as with the sharing bar.
    var script =
        "var input='',shown=false;process.stdin.on('data',function(d){input+=d.toString();if(input.length>16384)process._exit();var n;while((n=input.indexOf('\\n'))>=0){var line=input.substring(0,n);input=input.substring(n+1);try{var q=JSON.parse(line);if(q.command=='cancel')process._exit();if(q.command=='show'&&!shown){shown=true;show(q);}}catch(e){process._exit();}}});" +
        'setTimeout(function(){process._exit();},' +
        (Math.max(1, Math.min(30, seconds || 30)) * 1000 + 1000) +
        ');' +
        'function done(v){process.stdout.write(' +
        JSON.stringify(marker) +
        "+v+'\\n');setTimeout(function(){process._exit();},100);}" +
        "function show(q){try{var m=require('_GenericMarshal'),u=m.CreateNativeProxy('user32.dll');u.CreateMethod('MessageBoxW');" +
        'var c=m.CreateVariable(q.caption,{wide:true}),t=m.CreateVariable(q.title,{wide:true});' +
        'try{' +
        "u.CreateMethod('CreateIconFromResourceEx');u.CreateMethod('FindWindowExW');u.CreateMethod('GetWindowThreadProcessId');u.CreateMethod('GetDlgItem');u.CreateMethod('SendMessageW');" +
        "var b=Buffer.from(q.logo,'base64'),ib=m.CreateVariable(b.length);b.copy(ib.toBuffer());" +
        'var large=u.CreateIconFromResourceEx(ib,b.length,1,0x00030000,32,32,0),small=u.CreateIconFromResourceEx(ib,b.length,1,0x00030000,16,16,0),tries=0;' +
        'var branding=setInterval(function(){try{var previous=0;for(var i=0;i<16;i++){var w=u.FindWindowExW(0,previous,0,t);if(!w.Val)break;previous=w;var pid=m.CreateVariable(4);u.GetWindowThreadProcessId(w,pid);if(pid.toBuffer().readUInt32LE()==process.pid){' +
        'u.SendMessageW(w,0x0080,0,small);u.SendMessageW(w,0x0080,1,large);var icon=u.GetDlgItem(w,20);if(icon.Val)u.SendMessageW(icon,0x0170,large,0);clearInterval(branding);break;}}}catch(e){}if(++tries>=50)clearInterval(branding);},40);' +
        '}catch(e){}' +
        'u.MessageBoxW.async(0,c,t,0x00051133).then(function(a){done(a.Val);},function(){done(0);});}catch(e){done(0);}}';
    function finish(allowed, reason) {
        if (settled) return;
        settled = true;
        if (allowed === true) result.resolve();
        else result.reject(reason || 'denied');
    }
    try {
        result.child = require('child_process').execFile(
            process.execPath,
            [process.execPath.split('\\').pop(), '-b64exec', Buffer.from(script).toString('base64')],
            { type: 1, uid: uid }
        );
        var output = '';
        result.child.stdout.on('data', function (data) {
            output = (output + data.toString()).slice(-4096);
            var match = new RegExp(marker + '([0-9]+)\\r?\\n').exec(output);
            if (match) finish(match[1] == '6', match[1] == '0' ? 'unavailable' : 'denied');
        });
        result.child.stderr.on('data', function () {});
        result.child.on('exit', function () {
            finish(false, 'unavailable');
        });
        // The retained native launcher has a bounded command-line buffer. Keep
        // display text/image data off that command line and on the private pipe.
        result.child.stdin.write(
            JSON.stringify({ command: 'show', title: title, caption: caption, logo: logo }) + '\n'
        );
    } catch (error) {
        finish(false, 'unavailable');
    }
    result.close = function () {
        if (closed) return;
        closed = true;
        if (result.child) {
            try {
                result.child.stdin.write('{"command":"cancel"}\n');
            } catch (error) {}
            var child = result.child;
            setTimeout(function () {
                try {
                    child.kill();
                } catch (error) {}
            }, 1000);
        }
    };
    return result;
}

module.exports = { create: create };
