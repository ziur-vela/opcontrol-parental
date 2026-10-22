// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

(function () {
    let socket = null;
    let sessionId = null;
    let currentKind = null;
    let heartbeat = null;

    function stopHeartbeat() {
        if (heartbeat) { clearInterval(heartbeat); heartbeat = null; }
    }

    function stopRequest(session, keepalive) {
        if (!session) return;
        const token = window.ocpAntiforgeryToken ? window.ocpAntiforgeryToken() : null;
        try {
            fetch('/api/remote/sessions/' + session + '/stop', {
                method: 'POST',
                credentials: 'same-origin',
                keepalive: keepalive === true,
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token || '' },
                body: 'null'
            }).catch(function () { });
        } catch (e) { }
    }

    function setStatus(id, text) {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
    }

    window.addEventListener('pagehide', function () { window.ocpRemote.stop(true, true); });
    window.addEventListener('beforeunload', function () { window.ocpRemote.stop(true, true); });

    window.ocpRemote = {
        async getConsent() {
            try {
                const response = await fetch('/api/remote/camera-consent', { credentials: 'same-origin' });
                if (!response.ok) return { accepted: false, version: 1, text: '' };
                return await response.json();
            } catch { return { accepted: false, version: 1, text: '' }; }
        },
        async acceptCameraConsent() {
            try {
                const response = await fetch('/api/remote/camera-consent', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': window.ocpAntiforgeryToken() || '' },
                    body: JSON.stringify({ accepted: true, version: 1 })
                });
                if (!response.ok) return false;
                const result = await response.json();
                return result.accepted === true;
            } catch { return false; }
        },
        async start(deviceId, kind, canvasId, statusId) {
            window.ocpRemote.stop(false);
            setStatus(statusId, (window.ocpStrings && window.ocpStrings.sessionCreating) || 'Creating session…');
            let created;
            try {
                created = await window.ocpJsonPost('/api/remote/sessions', { deviceId: deviceId, kind: kind });
            } catch (e) {
                setStatus(statusId, (window.ocpStrings && window.ocpStrings.sessionStartFailed) || 'Could not start: ' + e.message);
                return false;
            }
            sessionId = created.id;
            currentKind = kind;
            const canvas = document.getElementById(canvasId);
            const ctx = canvas.getContext('2d');
            canvas.width = 640; canvas.height = 360;
            ctx.fillStyle = '#0b1714'; ctx.fillRect(0, 0, canvas.width, canvas.height);
            setStatus(statusId, (window.ocpStrings && window.ocpStrings.sessionWaiting) || 'Waiting for the device…');
            const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
            socket = new WebSocket(scheme + '://' + location.host + '/ws/remote/' + created.id);
            socket.binaryType = 'arraybuffer';
            socket.onmessage = async (event) => {
                if (typeof event.data === 'string') {
                    try {
                        const msg = JSON.parse(event.data);
                        if (msg.message) setStatus(statusId, msg.message);
                    } catch { }
                    return;
                }
                try {
                    const blob = new Blob([event.data], { type: 'image/jpeg' });
                    const bitmap = await createImageBitmap(blob);
                    if (canvas.width !== bitmap.width || canvas.height !== bitmap.height) {
                        canvas.width = bitmap.width;
                        canvas.height = bitmap.height;
                    }
                    ctx.drawImage(bitmap, 0, 0);
                    bitmap.close();
                    setStatus(statusId, kind === 1 ? ((window.ocpStrings && window.ocpStrings.cameraLive) || 'Camera live') : ((window.ocpStrings && window.ocpStrings.screenLive) || 'Screen live'));
                } catch { }
            };
            socket.onopen = () => {
                stopHeartbeat();
                heartbeat = setInterval(function () {
                    if (socket && socket.readyState === 1) {
                        try { socket.send(JSON.stringify({ type: 'heartbeat' })); } catch (e) { }
                    }
                }, 5000);
            };
            socket.onclose = () => {
                stopHeartbeat();
                setStatus(statusId, (window.ocpStrings && window.ocpStrings.sessionEnded) || 'Session ended.');
                socket = null; sessionId = null; currentKind = null;
            };
            socket.onerror = () => setStatus(statusId, (window.ocpStrings && window.ocpStrings.connectionError) || 'Connection error.');
            return true;
        },
        stop(sendStop, keepalive) {
            stopHeartbeat();
            if (sendStop !== false && sessionId) {
                stopRequest(sessionId, keepalive === true);
            }
            if (socket) {
                try { socket.close(); } catch (e) { }
                socket = null;
            }
            sessionId = null; currentKind = null;
        },
        fullscreen(elementId) {
            const el = document.getElementById(elementId);
            if (el && el.requestFullscreen) el.requestFullscreen().catch(() => { });
        }
    };
})();
