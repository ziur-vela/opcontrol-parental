// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

(function () {
    function apply(theme) {
        document.body.classList.remove('t-dark', 't-light');
        document.body.classList.add('t-' + theme);
        document.documentElement.style.colorScheme = theme;
    }
    try {
        apply(localStorage.getItem('ocp-theme') === 'light' ? 'light' : 'dark');
    } catch (e) {
        apply('dark');
    }
    window.ocpSetLanguageCookie = function (lang) {
        document.cookie = 'ocp.lang=' + encodeURIComponent(lang) + '; path=/; max-age=31536000; samesite=lax';
    };
    window.ocpToggleTheme = function () {
        var next = document.body.classList.contains('t-light') ? 'dark' : 'light';
        apply(next);
        try { localStorage.setItem('ocp-theme', next); } catch (e) { }
    };
    window.ocpAntiforgeryToken = function () {
        var input = document.querySelector('#af-form input[name="__RequestVerificationToken"], input[name="__RequestVerificationToken"]');
        return input ? input.value : null;
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
            throw new Error(text || ('HTTP ' + response.status));
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

    window.ocpComposer = {
        attach(id, dotNetRef, enterToSend) {
            var el = document.getElementById(id);
            if (!el) return;
            el.dataset.enterToSend = enterToSend ? 'true' : 'false';
            if (el.__ocpComposerHandler) el.removeEventListener('keydown', el.__ocpComposerHandler);
            el.__ocpComposerHandler = function (event) {
                if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return;
                if (el.dataset.enterToSend !== 'true') return;
                event.preventDefault();
                dotNetRef.invokeMethodAsync('SendFromEnterAsync', el.value).catch(function () { });
            };
            el.addEventListener('keydown', el.__ocpComposerHandler);
        },
        setEnterToSend(id, enabled) {
            var el = document.getElementById(id);
            if (el) el.dataset.enterToSend = enabled ? 'true' : 'false';
        },
        detach(id) {
            var el = document.getElementById(id);
            if (!el || !el.__ocpComposerHandler) return;
            el.removeEventListener('keydown', el.__ocpComposerHandler);
            el.__ocpComposerHandler = null;
        }
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
