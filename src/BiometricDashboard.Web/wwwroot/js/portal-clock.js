(function () {
    function pad(n) {
        return String(n).padStart(2, "0");
    }
    function tick() {
        const gateDate = document.getElementById("gateosDate");
        const gateTime = document.getElementById("gateosTime");
        if (gateDate && gateTime) {
            const d = new Date();
            const pad = (n) => String(n).padStart(2, "0");
            gateTime.textContent = `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
            gateDate.textContent = d.toLocaleDateString("en-GB", {
                weekday: "short",
                day: "numeric",
                month: "short",
                year: "numeric",
            });
            return;
        }

        const el = document.getElementById("portalClock");
        if (!el) return;
        const d = new Date();
        const days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

        if (el.classList.contains("mac-menubar-clock")) {
            const h = d.getHours();
            const h12 = h % 12 || 12;
            const ampm = h >= 12 ? "PM" : "AM";
            el.textContent =
                days[d.getDay()] + " " + d.getDate() + " " + months[d.getMonth()] +
                "  " + h12 + ":" + pad(d.getMinutes()) + " " + ampm;
            return;
        }

        el.innerHTML =
            `<span class="portal-clock-date"><i class="bi bi-calendar3"></i> ${days[d.getDay()]}, ${d.getDate()} ${months[d.getMonth()]} ${d.getFullYear()}</span>` +
            `<span class="portal-clock-time">${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}</span>`;
    }
    tick();
    setInterval(tick, 1000);
})();
