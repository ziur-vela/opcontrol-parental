(function () {
    let socket = null;
    let sessionId = null;
    let currentKind = null;

    function setStatus(id, text) {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
    }

    window.addEventListener('pagehide', function () { window.ocpRemote.stop(true); });

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
            setStatus(statusId, 'Creating session…');
            let created;
            try {
                created = await window.ocpJsonPost('/api/remote/sessions', { deviceId: deviceId, kind: kind });
            } catch (e) {
                setStatus(statusId, 'Could not start: ' + e.message);
                return false;
            }
            sessionId = created.id;
            currentKind = kind;
            const canvas = document.getElementById(canvasId);
            const ctx = canvas.getContext('2d');
            canvas.width = 640; canvas.height = 360;
            ctx.fillStyle = '#0b1714'; ctx.fillRect(0, 0, canvas.width, canvas.height);
            setStatus(statusId, 'Waiting for the device…');
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
                    setStatus(statusId, kind === 1 ? 'Camera live' : 'Screen live');
                } catch { }
            };
            socket.onclose = () => {
                setStatus(statusId, 'Session ended.');
                socket = null; sessionId = null; currentKind = null;
            };
            socket.onerror = () => setStatus(statusId, 'Connection error.');
            return true;
        },
        stop(sendStop) {
            if (sendStop !== false && sessionId) {
                window.ocpJsonPost('/api/remote/sessions/' + sessionId + '/stop', null).catch(() => { });
            }
            if (socket) {
                socket.close();
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
