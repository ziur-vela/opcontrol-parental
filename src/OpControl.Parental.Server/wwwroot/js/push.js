// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

// Installable app (PWA) and Web Push for the parent dashboard.
(function () {
    var installEvent = null;
    var registration = null;

    window.addEventListener('beforeinstallprompt', function (e) {
        e.preventDefault();
        installEvent = e;
    });
    window.addEventListener('appinstalled', function () { installEvent = null; });

    function supported() {
        return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
    }

    function isIos() {
        return /iphone|ipad|ipod/i.test(navigator.userAgent)
            || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    }

    function standalone() {
        return window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
    }

    function keyToBytes(base64) {
        var padding = '='.repeat((4 - base64.length % 4) % 4);
        var raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
        var out = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
        return out;
    }

    function deviceLabel() {
        var ua = navigator.userAgent;
        var browser = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox'
            : /SamsungBrowser/.test(ua) ? 'Samsung Internet' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
        var os = /Android/.test(ua) ? 'Android' : isIos() ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'macOS'
            : /CrOS/.test(ua) ? 'ChromeOS' : /Linux/.test(ua) ? 'Linux' : '';
        return (browser + (os ? ' · ' + os : '') + (standalone() ? ' · app' : '')).slice(0, 200);
    }

    async function ready() {
        if (registration) return registration;
        await navigator.serviceWorker.register('/sw.js', { scope: '/' });
        registration = await navigator.serviceWorker.ready;
        return registration;
    }

    async function send(sub) {
        var json = sub.toJSON();
        await window.ocpJsonPost('/api/push/subscribe', {
            endpoint: json.endpoint,
            keys: { p256dh: json.keys.p256dh, auth: json.keys.auth },
            deviceLabel: deviceLabel()
        });
    }

    async function currentSubscription() {
        if (!supported()) return null;
        var reg = await ready();
        return reg.pushManager.getSubscription();
    }

    window.ocpPush = {
        state: async function () {
            var sub = null;
            try { sub = await currentSubscription(); } catch (e) { }
            return {
                supported: supported(),
                permission: 'Notification' in window ? Notification.permission : 'unsupported',
                subscribed: !!sub,
                endpoint: sub ? sub.endpoint : null,
                ios: isIos(),
                standalone: standalone(),
                canInstall: !!installEvent
            };
        },
        enable: async function () {
            try {
                if (!supported()) return 'unsupported';
                var permission = await Notification.requestPermission();
                if (permission !== 'granted') return 'denied';
                var reg = await ready();
                var key = await fetch('/api/push/key', { credentials: 'same-origin' }).then(function (r) {
                    if (!r.ok) throw new Error('HTTP ' + r.status);
                    return r.json();
                });
                var sub = await reg.pushManager.getSubscription();
                if (sub) {
                    var current = sub.options && sub.options.applicationServerKey ? new Uint8Array(sub.options.applicationServerKey) : null;
                    var wanted = keyToBytes(key.publicKey);
                    var same = current && current.length === wanted.length && current.every(function (b, i) { return b === wanted[i]; });
                    if (!same) { await sub.unsubscribe(); sub = null; }
                }
                if (!sub) sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyToBytes(key.publicKey) });
                await send(sub);
                return null;
            } catch (e) {
                return (e && e.message) || 'error';
            }
        },
        disable: async function () {
            try {
                var sub = await currentSubscription();
                if (!sub) return null;
                var endpoint = sub.endpoint;
                await sub.unsubscribe();
                await window.ocpJsonPost('/api/push/unsubscribe', { endpoint: endpoint });
                return null;
            } catch (e) {
                return (e && e.message) || 'error';
            }
        },
        test: async function () {
            try {
                var sub = await currentSubscription();
                if (!sub) return 'not-subscribed';
                try {
                    await window.ocpJsonPost('/api/push/test', { endpoint: sub.endpoint });
                } catch (e) {
                    if (!(e && e.data && e.data.renew)) throw e;
                    // The push service dropped this device. Make a fresh subscription and try once more.
                    try { await sub.unsubscribe(); } catch (x) { }
                    var again = await window.ocpPush.enable();
                    if (again) return again;
                    sub = await currentSubscription();
                    if (!sub) return 'not-subscribed';
                    await window.ocpJsonPost('/api/push/test', { endpoint: sub.endpoint });
                }
                return null;
            } catch (e) {
                return (e && e.message) || 'error';
            }
        },
        install: async function () {
            if (!installEvent) return false;
            installEvent.prompt();
            var choice = await installEvent.userChoice;
            installEvent = null;
            return choice && choice.outcome === 'accepted';
        }
    };

    // Register the service worker on every page (makes the dashboard installable), and keep an
    // existing subscription known to the server, e.g. after the browser renewed it.
    if ('serviceWorker' in navigator) {
        window.addEventListener('load', async function () {
            try {
                await ready();
                if (!supported() || Notification.permission !== 'granted' || !window.ocpAntiforgeryToken()) return;
                var sub = await registration.pushManager.getSubscription();
                if (sub && !sessionStorage.getItem('ocp-push-synced')) {
                    await send(sub);
                    sessionStorage.setItem('ocp-push-synced', '1');
                }
            } catch (e) { }
        });
    }
})();
