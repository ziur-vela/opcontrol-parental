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
})();
