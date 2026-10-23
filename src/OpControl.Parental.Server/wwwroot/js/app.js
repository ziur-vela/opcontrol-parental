// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

(function () {
    function saved() {
        try { if (localStorage.getItem('ocp-theme') === 'light') return 'light'; } catch (e) { }
        return /(?:^|;\s*)ocp\.theme=light(?:;|$)/.test(document.cookie) ? 'light' : 'dark';
    }
    function remember(theme) {
        try { localStorage.setItem('ocp-theme', theme); } catch (e) { }
        document.cookie = 'ocp.theme=' + theme + '; path=/; max-age=31536000; samesite=lax';
    }
    function apply(theme) {
        if (!document.body.classList.contains('t-' + theme)) {
            document.body.classList.remove('t-dark', 't-light');
            document.body.classList.add('t-' + theme);
        }
        document.documentElement.style.colorScheme = theme;
        var meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.setAttribute('content', theme === 'light' ? '#f1f4f1' : '#0b1714');
    }
    var initial = saved();
    remember(initial);
    apply(initial);
    // Page changes inside the dashboard patch the body class back to what the server sent.
    // Put the parent's choice back after every one of them, and whenever the class is reset.
    function reapply() { apply(saved()); }
    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', reapply);
    } else {
        document.addEventListener('DOMContentLoaded', function () {
            if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', reapply);
        });
    }
    new MutationObserver(reapply).observe(document.body, { attributes: true, attributeFilter: ['class'] });
    window.ocpSetLanguageCookie = function (lang) {
        document.cookie = 'ocp.lang=' + encodeURIComponent(lang) + '; path=/; max-age=31536000; samesite=lax';
    };
    window.ocpToggleTheme = function () {
        var next = document.body.classList.contains('t-light') ? 'dark' : 'light';
        remember(next);
        apply(next);
    };
    window.ocpAntiforgeryToken = function () {
        var input = document.querySelector('#af-form input[name="__RequestVerificationToken"], input[name="__RequestVerificationToken"]');
        return input ? input.value : null;
    };
    window.ocpReadTextarea = function (el) {
        return el && typeof el.value === 'string' ? el.value : '';
    };
    window.ocpJsonPost = async function (url, body) {
        var token = window.ocpAntiforgeryToken();
        var response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token || '' },
            body: body ? JSON.stringify(body) : null
        });
        if (!response.ok) {
            var text = await response.text();
            var message = text;
            var data = null;
            try { data = JSON.parse(text); if (data && typeof data.error === 'string') message = data.error; } catch (e) { data = null; }
            var error = new Error(message || ('HTTP ' + response.status));
            error.data = data;
            throw error;
        }
        return response.json();
    };
    window.ocpSaveDeviceNotice = async function (deviceId, screenNotice, cameraNotice, hasCameraLightOrShutter) {
        await window.ocpJsonPost('/api/devices/' + deviceId + '/notices', {
            screenNotice: screenNotice,
            cameraNotice: cameraNotice,
            hasCameraLightOrShutter: hasCameraLightOrShutter
        });
        return true;
    };

    window.ocpRequestUpdate = async function (button, deviceId) {
        var original = button.textContent;
        button.disabled = true;
        button.textContent = (window.ocpStrings && window.ocpStrings.requesting) || 'Requesting…';
        try {
            await window.ocpJsonPost('/api/devices/' + deviceId + '/update');
            button.textContent = (window.ocpStrings && window.ocpStrings.updateRequested) || 'Update requested';
        } catch (e) {
            button.textContent = original;
            button.disabled = false;
            alert(((window.ocpStrings && window.ocpStrings.updateFailed) || 'Could not request the update: ') + e.message);
        }
    };
})();
