(function () {
    var key = "turno-theme";

    function stored() {
        try {
            return window.localStorage.getItem(key);
        } catch (error) {
            return null;
        }
    }

    function prefersDark() {
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
    }

    function apply(theme) {
        document.documentElement.dataset.theme = theme;
    }

    apply(stored() || (prefersDark() ? "dark" : "light"));

    window.turno = {
        isDark: function () {
            return document.documentElement.dataset.theme === "dark";
        },
        toggleTheme: function () {
            var next = window.turno.isDark() ? "light" : "dark";
            apply(next);
            try {
                window.localStorage.setItem(key, next);
            } catch (error) {
            }
            return next === "dark";
        }
    };
})();
