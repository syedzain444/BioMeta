(function () {
    "use strict";
    function pad(n) {
        return String(n).padStart(2, "0");
    }
    function tick() {
        var el = document.getElementById("loginMenubarClock");
        if (!el) return;
        var d = new Date();
        var days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        var months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        el.innerHTML =
            '<span class="portal-clock-date"><i class="bi bi-calendar3"></i> ' +
            days[d.getDay()] + ", " + d.getDate() + " " + months[d.getMonth()] + " " + d.getFullYear() +
            '</span><span class="portal-clock-time">' +
            pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds()) +
            "</span>";
    }
    tick();
    setInterval(tick, 1000);
})();
