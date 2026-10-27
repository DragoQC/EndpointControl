/* EndpointControl screen approval. Copyright DragoQC and contributors. MIT. */
// Keep approval independent of avatars, capture initialization and service-session UI.
var sessions = require('user-sessions');
var requests = {};
var grants = {};
var timeoutSeconds = 30;
var grantLifetime = 15000;
var maximumPrompts = 4;

function valid(data) {
    return (
        data &&
        typeof data.sessionid == 'string' &&
        data.sessionid.length <= 64 &&
        typeof data.responseid == 'string' &&
        data.responseid.length <= 64 &&
        typeof data.nonce == 'string' &&
        /^[0-9a-f]{32}$/.test(data.nonce) &&
        ['desktop', 'rdp', 'vnc', 'amt'].indexOf(data.protocol) >= 0 &&
        typeof data.rights == 'number' &&
        (data.rights & 8) != 0
    );
}

function windowsSession() {
    var users = sessions.Current();
    var consoleId = null;
    try {
        consoleId = sessions.consoleUid();
    } catch (e) {}
    var active = [];
    for (var id in users) {
        var user = users[id];
        if (
            user &&
            user.State == 'Active' &&
            typeof user.Username == 'string' &&
            user.Username.length > 0 &&
            user.SessionId > 0
        ) {
            if (user.SessionId == consoleId) return user;
            active.push(user);
        }
    }
    // Do not guess between multiple users or dispatch to session 0 / a logon screen.
    if (active.length != 1) throw new Error('No unambiguous interactive user');
    return active[0];
}

function target() {
    if (process.platform == 'win32') return windowsSession();
    return { SessionId: sessions.consoleUid(), Username: '', Domain: '' };
}

function sameSession(grant) {
    if (process.platform != 'win32') return true;
    try {
        var current = sessions.Current()[grant.uid];
        return (
            current &&
            current.State == 'Active' &&
            current.Username == grant.user &&
            current.Domain == grant.domain
        );
    } catch (e) {
        return false;
    }
}

function promptMessage(data) {
    var account = String(data.realname || data.username || 'Administrator')
        .substring(0, 100)
        .replace(/[\r\n\t]/g, ' ');
    var action =
        data.protocol == 'desktop'
            ? data.viewOnly === true
                ? 'view your screen'
                : 'view and control your screen'
            : 'use ' + data.protocol.toUpperCase() + ' on this computer';
    return (
        account +
        ' is requesting permission to ' +
        action +
        '.\n\nAllow this session?\n\nThis request expires after ' +
        timeoutSeconds +
        ' seconds. Choosing No, closing this window, or not responding denies access.'
    );
}

function request(data, send) {
    if (!valid(data)) return;
    for (var grantId in grants) if (grants[grantId].expires <= Date.now()) delete grants[grantId];
    if (requests[data.nonce]) return;
    var entry = { data: data, prompt: null, done: false, timer: null, user: null };
    function finish(allowed, reason) {
        if (entry.done) return;
        entry.done = true;
        if (entry.timer) clearTimeout(entry.timer);
        delete requests[data.nonce];
        if (allowed && entry.user) {
            grants[data.nonce] = {
                sessionid: data.sessionid,
                protocol: data.protocol,
                actor: data.username,
                uid: entry.user.SessionId,
                user: entry.user.Username,
                domain: entry.user.Domain,
                expires: Date.now() + grantLifetime
            };
        }
        try {
            send({
                action: 'msg',
                type: 'meshall-consent',
                sessionid: data.sessionid,
                responseid: data.responseid,
                nonce: data.nonce,
                protocol: data.protocol,
                granted: allowed === true,
                reason: reason,
                targetSession: entry.user ? entry.user.SessionId : null
            });
        } catch (e) {
            delete grants[data.nonce];
        } finally {
            if (entry.prompt && entry.prompt.close) {
                try {
                    entry.prompt.close();
                } catch (e) {}
            }
        }
    }
    entry.finish = finish;
    if (Object.keys(requests).length >= maximumPrompts) {
        finish(false, 'busy');
        return;
    }
    requests[data.nonce] = entry;
    entry.timer = setTimeout(function () {
        finish(false, 'timeout');
    }, timeoutSeconds * 1000);
    try {
        entry.user = target();
        // The retained native MessageBox is dispatched into the selected user's session,
        // defaults to No, and has no remembered/automatic acceptance option.
        entry.prompt = require(process.platform == 'win32' ? 'meshall-consent-dialog' : 'message-box').create(
            'EndpointControl - screen access request',
            promptMessage(data),
            timeoutSeconds,
            null,
            entry.user.SessionId
        );
        entry.prompt.then(
            function () {
                finish(true, 'approved');
            },
            function (reason) {
                finish(false, reason == 'unavailable' ? 'unavailable' : 'denied');
            }
        );
    } catch (e) {
        finish(false, 'unavailable');
    }
}

function consume(nonce, sessionid, protocol, actor) {
    var grant = grants[nonce];
    delete grants[nonce];
    if (
        !grant ||
        grant.expires <= Date.now() ||
        grant.sessionid != sessionid ||
        grant.protocol != protocol ||
        grant.actor != actor ||
        !sameSession(grant)
    )
        return null;
    return grant;
}

function cancel(data) {
    var entry = requests[data.nonce];
    if (entry && entry.data.sessionid == data.sessionid) entry.finish(false, 'canceled');
    var grant = grants[data.nonce];
    if (grant && grant.sessionid == data.sessionid) delete grants[data.nonce];
}

function reset() {
    grants = {};
    var nonces = Object.keys(requests);
    for (var i = 0; i < nonces.length; i++) requests[nonces[i]].finish(false, 'session-changed');
}

sessions.on('changed', reset);
sessions.on('locked', reset);
module.exports = { request: request, consume: consume, cancel: cancel, reset: reset };
