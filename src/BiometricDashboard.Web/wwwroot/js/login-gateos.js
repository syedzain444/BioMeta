(function () {
    "use strict";

    var timeEl = document.getElementById("time");
    var dateEl = document.getElementById("date");
    if (timeEl && dateEl) {
        function tick() {
            var n = new Date();
            var p = function (v) {
                return String(v).padStart(2, "0");
            };
            timeEl.innerHTML =
                p(n.getHours()) + ":" + p(n.getMinutes()) + '<span class="sec">' + p(n.getSeconds()) + "</span>";
            dateEl.textContent = n.toLocaleDateString("en-GB", {
                weekday: "long",
                day: "numeric",
                month: "long",
                year: "numeric",
            });
        }
        tick();
        setInterval(tick, 1000);
    }

    var empId = document.getElementById("empId");
    var pwd = document.getElementById("pwd");
    var avatar = document.getElementById("avatar");
    var initials = document.getElementById("initials");
    var who = document.getElementById("who");
    if (empId && avatar && initials && who) {
        function syncAvatar() {
            var v = empId.value.trim();
            avatar.classList.toggle("has-id", !!v);
            initials.textContent = v.slice(0, 2).toUpperCase();
            who.textContent = v ? v.toUpperCase() : "Welcome back";
        }
        empId.addEventListener("input", syncAvatar);
        syncAvatar();
    }

    var eye = document.getElementById("eye");
    if (eye && pwd) {
        eye.addEventListener("click", function () {
            var show = pwd.type === "password";
            pwd.type = show ? "text" : "password";
            eye.setAttribute("aria-pressed", show ? "true" : "false");
            eye.setAttribute("aria-label", show ? "Hide password" : "Show password");
        });
    }

    var caps = document.getElementById("caps");
    if (caps && pwd) {
        var capsCheck = function (e) {
            caps.classList.toggle("show", !!(e.getModifierState && e.getModifierState("CapsLock")));
        };
        pwd.addEventListener("keydown", capsCheck);
        pwd.addEventListener("keyup", capsCheck);
        pwd.addEventListener("blur", function () {
            caps.classList.remove("show");
        });
    }

    var card = document.getElementById("card");
    var msg = document.getElementById("msg");
    var form = document.getElementById("loginForm");
    if (form && card && msg) {
        form.addEventListener("submit", function (e) {
            if (!empId || !pwd) return;
            if (!empId.value.trim() || !pwd.value) {
                e.preventDefault();
                msg.textContent = empId.value.trim() ? "Enter your password." : "Enter your employee ID.";
                card.classList.remove("shake");
                void card.offsetWidth;
                card.classList.add("shake");
                (empId.value.trim() ? pwd : empId).focus();
            } else if (!msg.textContent) {
                msg.textContent = "";
            }
        });

        if (msg.textContent && msg.textContent.trim()) {
            card.classList.add("shake");
        }
    }
})();
