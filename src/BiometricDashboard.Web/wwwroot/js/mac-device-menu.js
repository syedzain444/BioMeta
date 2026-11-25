(function () {
    "use strict";
    document.addEventListener("DOMContentLoaded", function () {
        var root = document.getElementById("macDeviceMenu");
        var btn = document.getElementById("macDeviceMenuBtn");
        var panel = document.getElementById("macDeviceMenuPanel");
        if (!root || !btn || !panel) return;

        function close() {
            panel.hidden = true;
            btn.setAttribute("aria-expanded", "false");
        }

        btn.addEventListener("click", function (e) {
            e.stopPropagation();
            var open = panel.hidden;
            panel.hidden = !open;
            btn.setAttribute("aria-expanded", open ? "true" : "false");
        });

        document.addEventListener("click", function () {
            close();
        });

        panel.addEventListener("click", function (e) {
            e.stopPropagation();
        });

        document.addEventListener("keydown", function (e) {
            if (e.key === "Escape") close();
        });
    });
})();
