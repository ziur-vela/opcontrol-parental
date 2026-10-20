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
    window.ocpRequestUpdate = async function (button, deviceId) {
        var original = button.textContent;
        button.disabled = true;
        button.textContent = 'Requesting…';
        try {
            await window.ocpJsonPost('/api/devices/' + deviceId + '/update');
            button.textContent = 'Update requested';
        } catch (e) {
            button.textContent = original;
            button.disabled = false;
            alert('Could not request the update: ' + e.message);
        }
    };
})();
