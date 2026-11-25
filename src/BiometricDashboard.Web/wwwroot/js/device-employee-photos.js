(function () {
    "use strict";

    var RECENT_KEY = "gateos.dep.recent";
    var MAX_RECENT = 8;

    function deviceId() {
        var sel = document.getElementById("depDeviceSelect");
        if (sel && sel.value) return parseInt(sel.value, 10);
        var v = document.getElementById("filterDeviceId");
        return v && v.value ? parseInt(v.value, 10) : null;
    }

    function setDeviceIdHidden(id) {
        var v = document.getElementById("filterDeviceId");
        if (v) v.value = id != null ? String(id) : "";
    }

    function parseDevicesJson() {
        var el = document.getElementById("devicesJson");
        if (!el || !el.textContent) return [];
        try {
            var raw = JSON.parse(el.textContent);
            return Array.isArray(raw) ? raw : [];
        } catch (e) {
            return [];
        }
    }

    function initDeviceSelect() {
        var sel = document.getElementById("depDeviceSelect");
        if (!sel) return;
        parseDevicesJson().forEach(function (d) {
            var opt = document.createElement("option");
            opt.value = String(d.deviceId != null ? d.deviceId : d.DeviceId);
            var name = d.deviceName != null ? d.deviceName : d.DeviceName;
            var ip = d.deviceIp != null ? d.deviceIp : d.DeviceIp;
            opt.textContent = name + " (" + ip + ")";
            sel.appendChild(opt);
        });
        var initial = deviceId();
        if (initial && sel.querySelector('option[value="' + initial + '"]')) {
            sel.value = String(initial);
        } else if (sel.options.length === 2) {
            sel.selectedIndex = 1;
            setDeviceIdHidden(parseInt(sel.value, 10));
        }
        sel.addEventListener("change", function () {
            var id = sel.value ? parseInt(sel.value, 10) : null;
            setDeviceIdHidden(id);
            var label = sel.options[sel.selectedIndex] ? sel.options[sel.selectedIndex].textContent : "";
            var title = document.getElementById("depTitleDev");
            if (title && label) title.textContent = label;
        });
    }

    function setStatus(msg, isErr) {
        var el = document.getElementById("depPhotoStatus");
        if (!el) return;
        if (!msg) {
            el.hidden = true;
            el.textContent = "";
            return;
        }
        el.textContent = msg;
        el.hidden = false;
        el.classList.toggle("dep-status-err", !!isErr);
    }

    function loadRecent() {
        try {
            var raw = localStorage.getItem(RECENT_KEY);
            return raw ? JSON.parse(raw) : [];
        } catch (e) {
            return [];
        }
    }

    function saveRecent(entry) {
        var list = loadRecent().filter(function (x) {
            return x.employeeId !== entry.employeeId || x.deviceId !== entry.deviceId;
        });
        list.unshift(entry);
        if (list.length > MAX_RECENT) list.length = MAX_RECENT;
        try {
            localStorage.setItem(RECENT_KEY, JSON.stringify(list));
        } catch (e) {}
        paintRecent(list);
    }

    function paintRecent(list) {
        var ul = document.getElementById("depRecentList");
        var clr = document.getElementById("btnDepClearRecent");
        if (!ul) return;
        ul.innerHTML = "";
        if (!list.length) {
            var li = document.createElement("li");
            li.className = "dep-recent-empty";
            li.textContent = "No recent lookups yet.";
            ul.appendChild(li);
            if (clr) clr.hidden = true;
            return;
        }
        if (clr) clr.hidden = false;
        list.forEach(function (item) {
            var li = document.createElement("li");
            var btn = document.createElement("button");
            btn.type = "button";
            btn.className = "dep-recent-item";
            btn.innerHTML =
                "<b>" +
                (item.name || item.employeeId) +
                "</b><span>" +
                item.employeeId +
                "</span>";
            btn.addEventListener("click", function () {
                if (item.deviceId) {
                    var sel = document.getElementById("depDeviceSelect");
                    if (sel) sel.value = String(item.deviceId);
                    setDeviceIdHidden(item.deviceId);
                }
                var q = document.getElementById("depPhotoQuery");
                if (q) q.value = item.employeeId;
                lookup();
            });
            li.appendChild(btn);
            ul.appendChild(li);
        });
    }

    function renderLookup(data) {
        var idle = document.getElementById("depFrameIdle");
        var img = document.getElementById("depPhotoImg");
        var ph = document.getElementById("depPhotoPlaceholder");
        var meta = document.getElementById("depHeroMeta");
        var wrap = document.getElementById("depFrameWrap");
        if (!data || !data.employee) return;

        if (idle) idle.hidden = true;
        if (meta) meta.hidden = false;
        if (wrap) wrap.classList.add("dep-frame-active");

        document.getElementById("depPhotoName").textContent = data.employee.name || "—";
        document.getElementById("depPhotoId").textContent = data.employee.employeeId || "—";
        document.getElementById("depPhotoFaces").textContent =
            "Face · " + (data.employee.faceTemplateCount != null ? data.employee.faceTemplateCount : "0");
        document.getElementById("depPhotoBio").textContent =
            data.employee.biometricLabel || data.employee.biometricStatus || "—";
        document.getElementById("depPhotoDevice").textContent =
            (data.employee.deviceName || "Terminal") +
            (data.employee.deviceIp ? " · " + data.employee.deviceIp : "");

        if (data.hasPhoto && data.photoUrl && img) {
            img.hidden = false;
            if (ph) ph.hidden = true;
            img.src = data.photoUrl + (data.photoUrl.indexOf("?") >= 0 ? "&" : "?") + "t=" + Date.now();
            img.alt = "Photo for " + (data.employee.name || data.employee.employeeId);
            setStatus("Loaded photo from device.", false);
        } else if (ph) {
            img.hidden = true;
            ph.hidden = false;
            document.getElementById("depPhotoPlaceholderText").textContent =
                data.photoHint || "No display photo on device.";
            setStatus(data.photoHint || "", false);
        }

        saveRecent({
            employeeId: data.employee.employeeId,
            name: data.employee.name,
            deviceId: deviceId(),
            hasPhoto: !!data.hasPhoto,
        });
    }

    function renderMatches(list) {
        var box = document.getElementById("depPhotoMatches");
        if (!box) return;
        box.innerHTML = "";
        box.hidden = false;
        var title = document.createElement("p");
        title.textContent = "Multiple matches — pick one:";
        box.appendChild(title);
        list.forEach(function (m) {
            var btn = document.createElement("button");
            btn.type = "button";
            btn.className = "dep-match-btn";
            btn.textContent = (m.displayName || "?") + " · " + (m.employeeId || "");
            btn.addEventListener("click", function () {
                document.getElementById("depPhotoQuery").value = m.employeeId || "";
                lookup();
            });
            box.appendChild(btn);
        });
    }

    async function lookup() {
        var qEl = document.getElementById("depPhotoQuery");
        var q = qEl && qEl.value ? qEl.value.trim() : "";
        if (!q) {
            setStatus("Enter employee ID or name.", true);
            return;
        }
        var devId = deviceId();
        if (!devId) {
            setStatus("Select a terminal.", true);
            return;
        }

        var matches = document.getElementById("depPhotoMatches");
        if (matches) {
            matches.hidden = true;
            matches.innerHTML = "";
        }

        setStatus("Contacting device via GetEmployee…", false);
        try {
            var url =
                "/Device/EmployeePhotos/Lookup?deviceId=" +
                encodeURIComponent(devId) +
                "&q=" +
                encodeURIComponent(q);
            var res = await fetch(url, { credentials: "same-origin" });
            var data = await res.json();
            if (data.ambiguous && data.matches && data.matches.length) {
                renderMatches(data.matches);
                setStatus("Choose an employee from the list.", false);
                return;
            }
            if (!data.ok) {
                setStatus(data.error || "Lookup failed.", true);
                return;
            }
            renderLookup(data);
        } catch (e) {
            setStatus(e.message || "Network error.", true);
        }
    }

    function bind() {
        document.getElementById("btnDepLookup")?.addEventListener("click", lookup);
        document.getElementById("depPhotoQuery")?.addEventListener("keydown", function (ev) {
            if (ev.key === "Enter") {
                ev.preventDefault();
                lookup();
            }
        });
        document.getElementById("btnDepClearRecent")?.addEventListener("click", function () {
            try {
                localStorage.removeItem(RECENT_KEY);
            } catch (e) {}
            paintRecent([]);
        });
    }

    function init() {
        initDeviceSelect();
        paintRecent(loadRecent());
        bind();
        var q = document.getElementById("depPhotoQuery");
        if (q && q.value.trim()) lookup();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();