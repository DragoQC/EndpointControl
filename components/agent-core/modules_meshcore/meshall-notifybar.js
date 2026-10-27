/* EndpointControl notification-bar lifecycle. Copyright DragoQC and contributors. MIT. */
// A Windows desktop/session change can terminate the UI helper. That is not the
// same action as a person pressing the sharing bar's close button.
var original = require('notifybar-desktop');
function notify(title, tsid, options) {
    title = 'EndpointControl · ' + title;
    if (process.platform != 'win32' || require('user-sessions').getProcessOwnerName(process.pid).tsid != 0) {
        original.MaxWidth = notify.MaxWidth;
        original.DefaultPinned = notify.DefaultPinned;
        return original(title, tsid, options);
    }
    var result = { _closed: false, _timer: null, child: null };
    var emitter = require('events').EventEmitter.call(result, true).createEvent('close');
    var sessions = require('user-sessions');
    var marker = 'MESHALL_USER_CLOSED_' + Date.now() + '_' + String(Math.random()).split('.').join('');
    var script =
        "var n=require('notifybar-desktop');n.MaxWidth=" +
        JSON.stringify(notify.MaxWidth || 0) +
        ';n.DefaultPinned=' +
        JSON.stringify(notify.DefaultPinned || false) +
        ';' +
        'var bar=n(' +
        JSON.stringify(title) +
        ',' +
        JSON.stringify(options || {}) +
        ');' +
        "if(bar._promise)bar._promise.then(function(){for(var i=0;i<bar._pumps.length;i++){bar._pumps[i].prependListener('message',function(m){if(m.message===273 && (m.wparam&65535)===65520)process.stdout.write(" +
        JSON.stringify(marker + '\n') +
        ');});}});' +
        "bar.on('close',function(){setTimeout(function(){process._exit();},30);});" +
        "require('DescriptorEvents').addDescriptor(require('util-descriptors').getProcessHandle(" +
        process.pid +
        ")).on('signaled',function(){process._exit();});";
    var encoded = Buffer.from(script).toString('base64');
    emitter.addMethod('close', function () {
        if (this._closed) return;
        this._closed = true;
        if (this._timer) clearTimeout(this._timer);
        sessions.removeListener('changed', this._changed);
        if (this.child) {
            this.child.removeAllListeners('exit');
            try {
                this.child.kill();
            } catch (e) {}
            this.child = null;
        }
    });
    result._userClose = function () {
        if (!this._closed) {
            this.close();
            this.emit('close');
        }
    };
    result._spawn = function () {
        this._timer = null;
        if (this._closed) return;
        if (this.child) {
            this.child.removeAllListeners('exit');
            try {
                this.child.kill();
            } catch (e) {}
        }
        try {
            var child = require('child_process').execFile(
                process.execPath,
                [process.execPath.split('\\').pop(), '-b64exec', encoded],
                { type: 1, uid: tsid }
            );
            this.child = child;
            child.parent = this;
            child._closeOutput = '';
            child.descriptorMetadata = 'EndpointControl sharing bar';
            child.stdout.on('data', function (data) {
                child._closeOutput = (child._closeOutput + data.toString()).slice(-4096);
                if (child._closeOutput.indexOf(marker) >= 0) child.parent._userClose();
            });
            child.stderr.on('data', function () {});
            child.on('exit', function () {
                if (!this.parent._closed) {
                    this.parent.child = null;
                    this.parent._schedule();
                }
            });
        } catch (e) {
            this._schedule();
        }
    };
    result._schedule = function () {
        if (!this._closed && !this._timer) {
            var self = this;
            this._timer = setTimeout(function () {
                self._spawn();
            }, 750);
        }
    };
    result._changed = function () {
        if (!result._closed) result._schedule();
    };
    sessions.on('changed', result._changed);
    result._spawn();
    return result;
}
Object.defineProperty(notify, 'MaxWidth', {
    get: function () {
        return original.MaxWidth;
    },
    set: function (value) {
        original.MaxWidth = value;
    }
});
Object.defineProperty(notify, 'DefaultPinned', {
    get: function () {
        return original.DefaultPinned;
    },
    set: function (value) {
        original.DefaultPinned = value;
    }
});
module.exports = notify;
