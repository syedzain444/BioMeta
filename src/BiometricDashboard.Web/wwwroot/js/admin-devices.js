(function () {
    "use strict";

    const cfg = window.adminDevicesConfig || {};

    function canManageDevices() {
        const flag = document.getElementById("adevGrid")?.dataset?.canManage;
        if (flag === "1") return true;
        if (flag === "0") return false;
        return cfg.canManage === true || cfg.canManage === "true";
    }

    function testBtnHtml(d) {
        if (d && d.testRun) {
            return '<i class="adev-spin" aria-hidden="true"></i>';
        }
        return '<svg aria-hidden="true"><use href="#adev-i-pulse"/></svg>';
    }

    function actsHtml(id, d) {
        if (!canManageDevices()) return "";
        d = d || find(id) || {};
        return (
            '<div class="adev-acts">' +
            '<button type="button" class="adev-act adev-a-t" data-test="' +
            id +
            '" aria-label="Test">' +
            testBtnHtml(d) +
            "</button>" +
            '<button type="button" class="adev-act adev-a-e" data-edit="' +
            id +
            '" aria-label="Edit">' +
            '<svg aria-hidden="true"><use href="#adev-i-edit"/></svg>' +
            "</button>" +
            '<button type="button" class="adev-act adev-a-d" data-del="' +
            id +
            '" aria-label="Delete">' +
            '<svg aria-hidden="true"><use href="#adev-i-trash"/></svg>' +
            "</button></div>"
        );
    }

    function locHtml(loc) {
        return (
            '<div class="adev-loc"><svg aria-hidden="true"><use href="#adev-i-pin"/></svg>' +
            esc(loc || "—") +
            "</div>"
        );
    }

    function latTagHtml(d) {
        if (!d || !d.testTag) return "";
        const ok = String(d.testTag).indexOf("ms") >= 0;
        return (
            '<span class="adev-tag ' +
            (ok ? "adev-t-lat" : "adev-t-lat-fail") +
            '">' +
            esc(d.testTag) +
            "</span>"
        );
    }

    function readDevicesFromJson() {
        const el = document.getElementById("adevDevicesJson");
        if (el && el.textContent && el.textContent.trim()) {
            try {
                const parsed = JSON.parse(el.textContent);
                if (Array.isArray(parsed)) return parsed;
            } catch (e) {
                console.warn("adevDevicesJson parse failed", e);
            }
        }
        return Array.isArray(cfg.devices) ? cfg.devices : [];
    }

    const grid = document.getElementById("adevGrid");
    let devices = [];

    function hydrateDevicesFromDom() {
        if (!grid) return;
        const nodes = grid.querySelectorAll(".adev-app[data-device-id]");
        if (!nodes.length) return;
        devices = Array.from(nodes).map(function (node) {
            return {
                id: Number(node.getAttribute("data-device-id")),
                name: node.getAttribute("data-name") || "",
                ip: node.getAttribute("data-ip") || "",
                port: Number(node.getAttribute("data-port") || 0),
                loc: node.getAttribute("data-loc") || "",
                serial: node.getAttribute("data-serial") || "—",
                primary: node.getAttribute("data-primary") || "—",
                online: node.getAttribute("data-online") === "true",
                active: node.getAttribute("data-active") === "true",
                lastSyncError: node.getAttribute("data-sync-error") || "",
                editUrl: node.getAttribute("data-edit-url") || "",
                deleteUrl: node.getAttribute("data-delete-url") || "",
            };
        });
    }

    devices = readDevicesFromJson().map(function (d) {
        return Object.assign({}, d);
    });
    if (!devices.length) {
        hydrateDevicesFromDom();
    }

    const foot = document.getElementById("adevFoot");
    const sheet = document.getElementById("adevSheet");
    const sheetBg = document.getElementById("adevSheetBg");
    const toastEl = document.getElementById("adevToast");
    const base = (cfg.testBase || "/Admin/Devices/Test/").replace(/\/?$/, "/");
    const formBase = (cfg.formBase || "/Admin/Devices/Form").replace(/\/?$/, "");
    const saveUrl = cfg.saveUrl || "/Admin/Devices/Save";
    const testDraftUrl = cfg.testDraftUrl || "/Admin/Devices/TestDraft";
    const TEST_TIMEOUT_MS = 180000;
    let testStepTimers = [];
    let lastTestDeviceId = null;
    let lastTestDeviceName = null;

    function esc(s) {
        return String(s ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function token() {
        const host = document.getElementById("devicesAntiForgery");
        const fromHost = host?.querySelector('input[name="__RequestVerificationToken"]')?.value;
        if (fromHost) return fromHost;
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || "";
    }

    function toast(msg) {
        if (!toastEl) return;
        const msgEl = document.getElementById("adevToastMsg");
        if (msgEl) msgEl.textContent = msg;
        else toastEl.textContent = msg;
        toastEl.classList.add("is-on");
        clearTimeout(toast._h);
        toast._h = setTimeout(function () {
            toastEl.classList.remove("is-on");
        }, 4000);
    }

    function find(id) {
        id = Number(id);
        return devices.find(function (d) {
            return d.id === id;
        });
    }

    function updateStats() {
        const nOn = document.getElementById("adevNOn");
        const nErr = document.getElementById("adevNErr");
        const nSync = document.getElementById("adevNSync");
        const nAll = document.getElementById("adevNAll");
        const cnt = document.getElementById("adevCnt");
        if (cnt) cnt.textContent = String(devices.length);
        if (nAll) nAll.textContent = String(devices.length);
        if (nOn) nOn.textContent = String(devices.filter(function (d) { return d.online; }).length);
        if (nSync) nSync.textContent = String(devices.filter(function (d) { return d.active; }).length);
        if (nErr) {
            nErr.textContent = String(
                devices.filter(function (d) {
                    return d.lastSyncError;
                }).length
            );
        }
    }

    function patchDeviceUi(id) {
        const d = find(id);
        if (!d || !grid) return;
        let tile = grid.querySelector('.adev-app[data-device-id="' + id + '"]');
        if (!tile) {
            const openBtn = grid.querySelector('button[data-open="' + id + '"]');
            tile = openBtn ? openBtn.closest(".adev-app") : null;
        }
        if (!tile) {
            render();
            return;
        }

        tile.setAttribute("data-device-id", String(id));
        tile.setAttribute("data-online", d.online ? "true" : "false");
        tile.setAttribute("data-sync-error", d.lastSyncError || "");
        tile.classList.toggle("on", !!d.online);
        tile.classList.toggle("off", !d.online);

        const ico = tile.querySelector(".adev-ico");
        if (ico) {
            ico.classList.toggle("on", !!d.online);
            ico.classList.toggle("off", !d.online);
        }

        const tagRow = tile.querySelector(".adev-tags");
        if (tagRow) {
            const onTag = tagRow.querySelector(".adev-t-on, .adev-t-off");
            if (onTag) {
                onTag.textContent = d.online ? "Online" : "Offline";
                onTag.className = "adev-tag " + (d.online ? "adev-t-on" : "adev-t-off");
            }
        }

        const actsNode = tile.querySelector(".adev-acts");
        if (!actsNode) {
            const info = tile.querySelector(".adev-info");
            const wrap = document.createElement("div");
            wrap.innerHTML = actsHtml(id, d);
            const acts = wrap.firstElementChild;
            if (acts && info) info.appendChild(acts);
            else if (acts) tile.appendChild(acts);
        } else {
            const testBtn = actsNode.querySelector("[data-test]");
            if (testBtn) testBtn.innerHTML = testBtnHtml(d);
        }

        updateStats();
    }

    function applyProbeResult(id, data) {
        const d = find(id);
        if (!d) return;
        d.online = !!data.online;
        d.testRun = false;
        if (data.tcpMs != null && data.online) {
            d.testTag = data.tcpMs + " ms";
        } else if (data.online) {
            d.testTag = "OK";
        } else {
            d.testTag = "Timeout";
        }
        const errMsg = data.message || data.error || "";
        if (errMsg) d.lastSyncError = data.online ? "" : errMsg;
        patchDeviceUi(id);
        const tags = document.querySelector('.adev-app[data-device-id="' + id + '"] .adev-tags');
        if (tags) {
            const oldLat = tags.querySelector(".adev-t-lat, .adev-t-lat-fail");
            if (oldLat) oldLat.remove();
            const wrap = document.createElement("div");
            wrap.innerHTML = latTagHtml(d);
            const lat = wrap.firstElementChild;
            if (lat) tags.appendChild(lat);
        }
    }

    function render() {
        if (!grid) return;
        const q = (document.getElementById("adevSearch")?.value || "").toLowerCase();
        const list = devices.filter(function (d) {
            return (d.name + d.ip + d.loc + (d.serial || "")).toLowerCase().indexOf(q) >= 0;
        });

        let html = list
            .map(function (d) {
                const c = d.online ? "on" : "off";
                const acts = actsHtml(d.id, d);
                const syncTag = d.active
                    ? '<span class="adev-tag adev-t-sync">Sync</span>'
                    : '<span class="adev-tag adev-t-offpool">Inactive</span>';
                return (
                    '<article class="adev-app ' +
                    c +
                    '" data-device-id="' +
                    d.id +
                    '">' +
                    '<button type="button" class="adev-ico ' +
                    c +
                    '" data-open="' +
                    d.id +
                    '" aria-label="Open ' +
                    esc(d.name) +
                    '">' +
                    '<svg aria-hidden="true"><use href="#adev-i-fp"/></svg>' +
                    '<span class="adev-dot" aria-hidden="true"></span></button>' +
                    '<div class="adev-info">' +
                    '<div class="adev-nm">' +
                    esc(d.name) +
                    "</div>" +
                    locHtml(d.loc) +
                    '<span class="adev-ip">' +
                    esc(d.ip) +
                    ":" +
                    d.port +
                    "</span>" +
                    '<div class="adev-tags"><span class="adev-tag ' +
                    (d.online ? "adev-t-on" : "adev-t-off") +
                    '">' +
                    (d.online ? "Online" : "Offline") +
                    "</span>" +
                    syncTag +
                    latTagHtml(d) +
                    "</div>" +
                    acts +
                    "</div></article>"
                );
            })
            .join("");

        if (canManageDevices() && !q) {
            html +=
                '<article class="adev-app adev-add" tabindex="0"><button type="button" class="adev-ico" data-device-add aria-label="Add device">' +
                '<svg aria-hidden="true"><use href="#adev-i-plus"/></svg></button>' +
                '<div class="adev-info"><div class="adev-nm">Add device</div>' +
                '<div class="adev-loc">Register a new terminal</div></div></article>';
        }

        if (!list.length && q) {
            html =
                '<div class="adev-empty">No device matches “' +
                esc(q) +
                '”. Try an IP, name or location.</div>' +
                html;
        }

        grid.innerHTML = html;
        if (foot) {
            foot.textContent = "Showing " + list.length + " of " + devices.length + " devices";
        }
        updateStats();
    }

    function openModal(title, bodyHtml, footHtml, options) {
        if (!sheet || !sheetBg) return;
        options = options || {};
        if (options.crud) {
            sheet.classList.remove("adev-sheet--test");
            sheet.classList.add("adev-sheet--crud");
            sheet.innerHTML = bodyHtml;
            sheetBg.classList.add("open", "adev-sheet-bg--mac");
            sheetBg.setAttribute("aria-hidden", "false");
            return;
        }
        if (options.test) {
            sheet.classList.remove("adev-sheet--crud");
            sheet.classList.add("adev-sheet--test");
            sheet.innerHTML = bodyHtml;
            sheetBg.classList.add("open", "adev-sheet-bg--mac");
            sheetBg.setAttribute("aria-hidden", "false");
            return;
        }
        sheet.classList.remove("adev-sheet--crud", "adev-sheet--test");
        const foot =
            footHtml != null && footHtml !== ""
                ? '<div class="adev-modal-foot">' + footHtml + "</div>"
                : "";
        sheet.innerHTML =
            '<div class="adev-modal-win" role="document">' +
            '<div class="adev-modal-tb">' +
            '<button type="button" class="adev-modal-lights adev-modal-close" id="adevModalCloseBtn" aria-label="Close">' +
            "<i></i><i></i><i></i></button>" +
            '<h2 class="adev-modal-title" id="adevModalTitle">' +
            esc(title) +
            "</h2>" +
            "<span></span></div>" +
            '<div class="adev-modal-body">' +
            bodyHtml +
            "</div>" +
            foot +
            "</div>";
        sheetBg.classList.add("open");
        sheetBg.classList.remove("adev-sheet-bg--mac");
        sheetBg.setAttribute("aria-hidden", "false");
        document.getElementById("adevModalCloseBtn")?.addEventListener("click", function (e) {
            e.preventDefault();
            e.stopPropagation();
            closeSheet();
        });
    }

    function openDeviceForm(id) {
        if (!canManageDevices()) return;
        const url = id ? formBase + "/" + id : formBase;
        openModal(
            "",
            '<div class="dmac-modal"><div class="dmac-load"><span class="dmac-spin"></span> Preparing form…</div></div>',
            "",
            { crud: true }
        );
        fetch(url, { credentials: "same-origin", headers: { Accept: "text/html" } })
            .then(function (res) {
                if (!res.ok) throw new Error("form");
                return res.text();
            })
            .then(function (html) {
                const title = id ? "Edit terminal" : "Register terminal";
                openModal(title, html, "", { crud: true });
                wireDeviceModalForm();
            })
            .catch(function () {
                toast("Could not open form");
                closeSheet();
            });
    }

    function setCrudRes(text, tone) {
        const r = document.getElementById("adevCrudTestRes");
        if (!r) return;
        r.classList.remove("dmac-on");
        if (!text) {
            r.textContent = "";
            return;
        }
        r.style.color = tone === "fail" ? "#dc2626" : tone === "warn" ? "#b45309" : "#0a9d63";
        r.textContent = text;
        r.classList.add("dmac-on");
    }

    function validateDeviceFormFields(form) {
        const ipEl = form.querySelector('[name="DeviceIp"]');
        const portEl = form.querySelector('[name="DevicePort"]');
        const ip = ipEl?.value?.trim() || "";
        const okIp =
            /^(\d{1,3}\.){3}\d{1,3}$/.test(ip) &&
            ip.split(".").every(function (n) {
                return Number(n) <= 255;
            });
        const pv = Number(portEl?.value);
        const okPort = pv > 0 && pv < 65536;
        const rowIp = form.querySelector('.dmac-rw[data-k="ip"]');
        const rowPort = form.querySelector('.dmac-rw[data-k="port"]');
        if (rowIp) rowIp.classList.toggle("dmac-bad", !okIp && ip !== "");
        if (rowPort) rowPort.classList.toggle("dmac-bad", !okPort && portEl?.value !== "");
        return okIp && okPort;
    }

    function syncMacSidePanel(form) {
        const name = form.querySelector('[name="DeviceName"]')?.value?.trim() || "Terminal";
        const ip = form.querySelector('[name="DeviceIp"]')?.value?.trim() || "—";
        const port = form.querySelector('[name="DevicePort"]')?.value?.trim() || "—";
        const n = document.getElementById("adevMacName");
        const i = document.getElementById("adevMacIp");
        const h = document.getElementById("adevMacHdcp");
        if (n) n.textContent = name;
        if (i) i.textContent = ip;
        if (h) h.innerHTML = port + "<small>pull · test</small>";
        validateDeviceFormFields(form);
    }

    function wireMacSwitch(swBtn, checkbox) {
        if (!swBtn || !checkbox) return;
        const sync = function () {
            swBtn.setAttribute("aria-checked", checkbox.checked ? "true" : "false");
        };
        sync();
        swBtn.addEventListener("click", function () {
            checkbox.checked = !checkbox.checked;
            sync();
        });
    }

    function wireDeviceModalForm() {
        const form = document.getElementById("deviceModalForm");
        if (!form) return;

        document.getElementById("adevFormCancel")?.addEventListener("click", function (e) {
            e.preventDefault();
            closeSheet();
        });
        document.getElementById("adevMacClose")?.addEventListener("click", function (e) {
            e.preventDefault();
            closeSheet();
        });
        document.getElementById("btnDeviceTestDraft")?.addEventListener("click", function (e) {
            e.preventDefault();
            runDeviceFormTest(form);
        });
        form.addEventListener("submit", function (e) {
            e.preventDefault();
            if (!validateDeviceFormFields(form)) {
                setCrudRes("Check IP and port", "fail");
                return;
            }
            submitDeviceForm(form);
        });

        const eye = document.getElementById("adevMacEye");
        const pw = form.querySelector('[name="AdminPassword"]');
        if (eye && pw) {
            eye.addEventListener("click", function () {
                const show = pw.type === "password";
                pw.type = show ? "text" : "password";
                const use = eye.querySelector("use");
                if (use) use.setAttribute("href", show ? "#dmac-i-eyeoff" : "#dmac-i-eye");
            });
        }

        wireMacSwitch(
            document.getElementById("swActive"),
            form.querySelector('input[type="checkbox"][name="IsActive"]')
        );
        wireMacSwitch(
            document.getElementById("swPrimary"),
            form.querySelector('input[type="checkbox"][name="IsPrimary"]')
        );

        ["DeviceIp", "DevicePort", "DeviceName"].forEach(function (field) {
            const el = form.querySelector('[name="' + field + '"]');
            if (el) el.addEventListener("input", function () {
                syncMacSidePanel(form);
            });
        });
        syncMacSidePanel(form);
    }

    function showFormErrors(form, data) {
        const box = document.getElementById("deviceModalValSummary");
        if (!box) return;
        const errors = data && data.errors ? data.errors : {};
        Object.keys(errors).forEach(function (key) {
            const input = form.querySelector('[name="' + key + '"]');
            const row = input?.closest(".dmac-rw");
            if (row) row.classList.add("dmac-bad");
        });
        const msgs = [];
        Object.keys(errors).forEach(function (key) {
            errors[key].forEach(function (m) {
                msgs.push(m);
            });
        });
        if (msgs.length) {
            box.hidden = false;
            box.innerHTML = "<strong>Could not save</strong><ul><li>" + msgs.map(esc).join("</li><li>") + "</li></ul>";
        } else {
            box.hidden = false;
            box.innerHTML = "<strong>" + esc(data?.message || "Save failed") + "</strong>";
        }
    }

    function clearFormErrors(form) {
        const box = document.getElementById("deviceModalValSummary");
        if (box) box.hidden = true;
        form.querySelectorAll(".dmac-rw.dmac-bad").forEach(function (el) {
            el.classList.remove("dmac-bad");
        });
        setCrudRes("");
    }

    async function submitDeviceForm(form) {
        clearFormErrors(form);
        const saveBtn = document.getElementById("adevFormSave");
        if (saveBtn) {
            saveBtn.disabled = true;
            saveBtn.classList.add("is-busy");
        }
        try {
            const fd = new FormData(form);
            const res = await fetch(saveUrl, {
                method: "POST",
                body: fd,
                credentials: "same-origin",
                headers: {
                    Accept: "application/json",
                    "X-Requested-With": "XMLHttpRequest",
                },
            });
            const text = await res.text();
            let data;
            try {
                data = text ? JSON.parse(text) : {};
            } catch {
                toast("Invalid response from server");
                return;
            }
            if (!res.ok || !data.ok) {
                showFormErrors(form, data);
                return;
            }
            if (data.device) {
                const idx = devices.findIndex(function (x) {
                    return x.id === data.device.id;
                });
                if (idx >= 0) devices[idx] = data.device;
                else devices.push(data.device);
            }
            closeSheet();
            render();
            toast(data.message || "Saved");
        } catch {
            toast("Save failed");
        } finally {
            if (saveBtn) {
                saveBtn.disabled = false;
                saveBtn.classList.remove("is-busy");
            }
        }
    }

    async function runDeviceFormTest(form) {
        const serialInput = document.getElementById("LastTestSerial");
        const testBtn = document.getElementById("btnDeviceTestDraft");
        const ip = form.querySelector('[name="DeviceIp"]')?.value?.trim();
        const port = form.querySelector('[name="DevicePort"]')?.value?.trim() || "9922";
        if (!validateDeviceFormFields(form)) {
            setCrudRes("Check IP and port", "fail");
            return;
        }
        if (!ip) {
            setCrudRes("Enter terminal IP first", "fail");
            return;
        }
        if (testBtn) {
            testBtn.disabled = true;
            testBtn.innerHTML = '<span class="dmac-spin"></span><span>Testing…</span>';
        }
        setCrudRes("");
        const ac = new AbortController();
        const timer = setTimeout(function () {
            ac.abort();
        }, TEST_TIMEOUT_MS);
        try {
            const fd = new FormData();
            fd.append("__RequestVerificationToken", token());
            fd.append("deviceIp", ip);
            fd.append("devicePort", port);
            const res = await fetch(testDraftUrl, {
                method: "POST",
                body: fd,
                credentials: "same-origin",
                headers: { Accept: "application/json" },
                signal: ac.signal,
            });
            const text = await res.text();
            let data;
            try {
                data = text ? JSON.parse(text) : {};
            } catch {
                setCrudRes("Invalid response (HTTP " + res.status + ")", "fail");
                return;
            }
            if (!res.ok) {
                setCrudRes(data.error || data.message || "Request failed", "fail");
                return;
            }
            const protocolOk = !!data.online;
            let msg;
            if (protocolOk) {
                msg =
                    "Connected · " +
                    (data.tcpMs != null ? data.tcpMs + " ms" : "OK");
                if (data.serial) msg += " · " + data.serial;
                setCrudRes(msg, "ok");
            } else if (data.tcpOk) {
                setCrudRes(data.message || "TCP OK — HDCP failed", "warn");
            } else {
                setCrudRes(data.message || data.error || "Connection failed", "fail");
            }
            if (serialInput && data.serial) serialInput.value = data.serial;
        } catch (e) {
            const msg = e && e.name === "AbortError" ? "Test timed out (3 min)." : e?.message || "Network error.";
            setCrudRes(msg, "fail");
        } finally {
            clearTimeout(timer);
            if (testBtn) {
                testBtn.disabled = false;
                testBtn.innerHTML =
                    '<svg><use href="#dmac-i-pulse"/></svg><span>Test connection</span>';
            }
        }
    }

    function openSheet(html, title) {
        openModal(title || "GateOS", html, "");
    }

    function closeSheet() {
        clearTestStepTimers();
        sheetBg?.classList.remove("open", "adev-sheet-bg--mac");
        sheetBg?.setAttribute("aria-hidden", "true");
        sheet?.classList.remove("adev-sheet--crud", "adev-sheet--test");
    }

    function clearTestStepTimers() {
        testStepTimers.forEach(clearTimeout);
        testStepTimers = [];
    }

    function dtstSvgDefs() {
        return (
            '<svg width="0" height="0" class="dtst-sprite" aria-hidden="true"><defs>' +
            '<symbol id="dtst-i-ok" viewBox="0 0 24 24"><path d="M5 12.500l4.500 4.500L19 7.500"/></symbol>' +
            '<symbol id="dtst-i-bolt" viewBox="0 0 24 24"><path d="M13 2L4 14h7l-1 8 9-12h-7z"/></symbol>' +
            '<symbol id="dtst-i-db" viewBox="0 0 24 24"><path d="M4 6c0-1.700 3.600-3 8-3s8 1.300 8 3-3.600 3-8 3-8-1.300-8-3zM4 6v6c0 1.700 3.600 3 8 3s8-1.300 8-3V6M4 12v6c0 1.700 3.600 3 8 3s8-1.300 8-3v-6"/></symbol>' +
            '<symbol id="dtst-i-globe" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 010 18M12 3a14 14 0 000 18"/></symbol>' +
            '<symbol id="dtst-i-net" viewBox="0 0 24 24"><path d="M7 7h13l-4-4M17 17H4l4 4"/></symbol>' +
            '<symbol id="dtst-i-info" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/></symbol>' +
            '<symbol id="dtst-i-cal" viewBox="0 0 24 24"><rect x="3" y="5" width="18" height="16" rx="3"/><path d="M3 10h18M8 3v4M16 3v4"/></symbol>' +
            '<symbol id="dtst-i-hash" viewBox="0 0 24 24"><path d="M5 9h14M5 15h14M10 3L8 21M16 3l-2 18"/></symbol>' +
            '<symbol id="dtst-i-sync" viewBox="0 0 24 24"><path d="M20 12a8 8 0 01-14 5.300M4 12a8 8 0 0114-5.300M18 3v4h-4M6 21v-4h4"/></symbol>' +
            "</defs></svg>"
        );
    }

    function buildConnectionTestModal(d, state) {
        state = state || "run";
        const endpoint = d.ip && d.port != null ? esc(d.ip) + ":" + d.port : "—";
        const serial = esc(d.serial && d.serial !== "—" ? d.serial : "—");
        const port = d.port != null ? d.port : 9922;
        const stepDefs = [
            ["TCP (HDCP)", "Socket handshake on port " + port, "dtst-i-net"],
            ["GetDeviceInfo", "Model, firmware and serial", "dtst-i-info"],
            ["GetRecord (7-day)", "Attendance logs, last 7 days", "dtst-i-db"],
        ];
        const stepsHtml = stepDefs
            .map(function (s) {
                return (
                    '<li class="dtst-st">' +
                    '<span class="dtst-sd"><svg class="dtst-i"><use href="#' +
                    s[2] +
                    '"/></svg><i class="dtst-o"></i><svg class="dtst-k"><use href="#dtst-i-ok"/></svg></span>' +
                    "<div><b>" +
                    esc(s[0]) +
                    "</b><small>" +
                    esc(s[1]) +
                    '</small></div><span class="dtst-rs"></span></li>'
                );
            })
            .join("");
        return (
            dtstSvgDefs() +
            '<section class="dtst-modal" id="adevTestModal" data-s="' +
            state +
            '" role="dialog" aria-label="Connection test">' +
            '<div class="dtst-tb"><div class="dtst-dots"><button type="button" id="adevTestCloseDot" aria-label="Close"></button><button type="button" tabindex="-1" aria-hidden="true"></button><button type="button" tabindex="-1" aria-hidden="true"></button></div>Connection test</div>' +
            '<div class="dtst-body"><div class="dtst-hero"><div class="dtst-badge"><svg class="dtst-ck"><use href="#dtst-i-ok"/></svg><i class="dtst-sp"></i></div>' +
            '<h1 id="adevTestHead">' +
            (state === "run" ? "Probing device…" : "Connection test") +
            '</h1><p id="adevTestSub">' +
            (state === "run" ? "Checking TCP, device info and records" : "") +
            "</p></div>" +
            '<div class="dtst-stats"><div class="dtst-stat"><span class="dtst-ri" style="background:#f59e0b"><svg><use href="#dtst-i-bolt"/></svg></span><div><small>Latency</small><b id="adevTestLat">—</b></div></div>' +
            '<div class="dtst-stat"><span class="dtst-ri" style="background:#6366f1"><svg><use href="#dtst-i-db"/></svg></span><div><small>Records</small><b id="adevTestRec">—</b></div></div></div>' +
            '<div class="dtst-cap">Device</div><div class="dtst-grp">' +
            '<div class="dtst-rw"><span class="dtst-ri" style="background:#14b8a6"><svg><use href="#dtst-i-hash"/></svg></span>Serial<span class="dtst-v"><span class="dtst-mono" id="adevTestSerial">' +
            serial +
            '</span></span></div><div class="dtst-rw"><span class="dtst-ri" style="background:#3b82f6"><svg><use href="#dtst-i-globe"/></svg></span>Endpoint<span class="dtst-v" id="adevTestEndpoint">' +
            endpoint +
            '</span></div><div class="dtst-rw"><span class="dtst-ri" style="background:#ef4444"><svg><use href="#dtst-i-cal"/></svg></span>Checked at<span class="dtst-v" id="adevTestTs" style="font-weight:500">—</span></div></div>' +
            '<div class="dtst-cap">HDCP probe · same as Edit device</div><div class="dtst-grp"><ol class="dtst-steps" id="adevTestSteps">' +
            stepsHtml +
            '</ol></div><div class="dtst-note"><svg><use href="#dtst-i-info"/></svg><span>TCP → GetDeviceInfo → GetRecord on HDCP port. Probe runs from the portal server (not your browser).</span></div></div>' +
            '<div class="dtst-foot"><button type="button" class="dtst-b" id="adevTestAgain"><svg><use href="#dtst-i-sync"/></svg>Run again</button>' +
            '<button type="button" class="dtst-b dtst-p" id="adevTestCloseBtn">Close</button></div></section>'
        );
    }

    function wireConnectionTestModal(deviceId, deviceName) {
        lastTestDeviceId = deviceId;
        lastTestDeviceName = deviceName;
        function closeTest(e) {
            e.preventDefault();
            e.stopPropagation();
            closeSheet();
        }
        document.getElementById("adevTestCloseBtn")?.addEventListener("click", closeTest);
        document.getElementById("adevTestCloseDot")?.addEventListener("click", closeTest);
        document.getElementById("adevTestAgain")?.addEventListener("click", function (e) {
            e.preventDefault();
            if (lastTestDeviceId != null) testDevice(lastTestDeviceId, lastTestDeviceName);
        });
    }

    function startConnectionTestStepAnimation() {
        clearTestStepTimers();
        document.querySelectorAll("#adevTestSteps .dtst-st").forEach(function (el, i) {
            el.className = "dtst-st";
            const rs = el.querySelector(".dtst-rs");
            if (rs) rs.textContent = "";
            testStepTimers.push(
                setTimeout(function () {
                    el.className = "dtst-st run";
                }, i * 750 + 100)
            );
        });
    }

    function stepLabelsFromProbe(data) {
        const tcp = data.tcpOk
            ? "OK" + (data.tcpMs != null ? " · " + data.tcpMs + " ms" : "")
            : "Failed";
        const info = data.online ? "OK" : data.tcpOk ? "Failed" : "Skipped";
        const rec = data.dataOk ? "OK" : data.online ? "Failed" : "Skipped";
        return [tcp, info, rec];
    }

    function animateTestRecCount(target) {
        const rec = document.getElementById("adevTestRec");
        if (!rec) return;
        let n = 0;
        const iv = setInterval(function () {
            n += Math.ceil((target - n) / 6);
            if (n >= target) {
                rec.textContent = target.toLocaleString();
                clearInterval(iv);
            } else {
                rec.textContent = n.toLocaleString();
            }
        }, 40);
    }

    function finishConnectionTestModal(data) {
        clearTestStepTimers();
        const modal = document.getElementById("adevTestModal");
        if (!modal) return;
        const protocolOk = !!data.online;
        const tcpOk = !!data.tcpOk;
        const state = protocolOk ? "ok" : tcpOk ? "warn" : "fail";
        modal.setAttribute("data-s", state);

        const head = document.getElementById("adevTestHead");
        const sub = document.getElementById("adevTestSub");
        if (head) {
            head.textContent = protocolOk
                ? "Device answered HDCP."
                : tcpOk
                  ? "TCP OK — HDCP failed"
                  : "Connection test failed";
        }
        if (sub) {
            var msg = data.message || data.error || "";
            if (!msg && protocolOk) msg = "Probe completed successfully.";
            sub.textContent = msg || "—";
        }

        const lat = document.getElementById("adevTestLat");
        if (lat) lat.textContent = data.tcpMs != null ? data.tcpMs + " ms" : "—";

        const recN =
            data.sampleRecords != null
                ? Number(data.sampleRecords)
                : data.recordsOnDevice != null
                  ? Number(data.recordsOnDevice)
                  : 0;
        if (protocolOk && recN > 0) animateTestRecCount(recN);
        else {
            const rec = document.getElementById("adevTestRec");
            if (rec) {
                if (recN > 0) rec.textContent = String(recN);
                else if (data.portalAttendanceRows != null && Number(data.portalAttendanceRows) > 0)
                    rec.textContent = Number(data.portalAttendanceRows).toLocaleString() + " in portal";
                else if (data.recordBuffer != null && Number(data.recordBuffer) > 0)
                    rec.textContent = Number(data.recordBuffer).toLocaleString() + " on device";
                else rec.textContent = "—";
            }
        }

        const serialEl = document.getElementById("adevTestSerial");
        if (serialEl && data.serial) serialEl.textContent = data.serial;
        const ep = document.getElementById("adevTestEndpoint");
        if (ep && data.host && data.port != null) ep.textContent = data.host + ":" + data.port;
        const ts = document.getElementById("adevTestTs");
        if (ts && data.checkedAt) {
            if (window.PortalDateTime) ts.innerHTML = PortalDateTime.formatHtml(data.checkedAt);
            else ts.textContent = data.checkedAt;
        }

        const labels = stepLabelsFromProbe(data);
        document.querySelectorAll("#adevTestSteps .dtst-st").forEach(function (el, i) {
            const fail = labels[i] === "Failed";
            el.className = fail ? "dtst-st fail done" : "dtst-st done";
            const rs = el.querySelector(".dtst-rs");
            if (rs) rs.textContent = labels[i];
        });
    }

    function openConnectionTestModal(deviceId, deviceName) {
        const d = find(deviceId) || { name: deviceName, ip: "", port: 9922, serial: "—" };
        openModal("", buildConnectionTestModal(d, "run"), "", { test: true });
        wireConnectionTestModal(deviceId, deviceName || d.name);
        startConnectionTestStepAnimation();
    }

    function openConnectionTestModalError(deviceId, deviceName, message) {
        const d = find(deviceId) || { name: deviceName, ip: "", port: 9922, serial: "—" };
        openModal("", buildConnectionTestModal(d, "fail"), "", { test: true });
        wireConnectionTestModal(deviceId, deviceName);
        finishConnectionTestModal({
            online: false,
            tcpOk: false,
            message: message,
            host: d.ip,
            port: d.port,
        });
    }

    function detail(id) {
        const d = find(id);
        if (!d) return;
        const c = d.online ? "on" : "off";
        const body =
            '<div class="adev-sh-head"><div class="adev-ico ' +
            c +
            '">' +
            '<svg aria-hidden="true"><use href="#adev-i-fp"/></svg>' +
            '</div><div><h3>' +
            esc(d.name) +
            '</h3><div class="adev-loc">' +
            esc(d.loc || "—") +
            "</div></div></div>" +
            '<div class="adev-rows">' +
            '<div class="adev-row"><span>IP : Port</span><b>' +
            esc(d.ip) +
            ":" +
            d.port +
            "</b></div>" +
            '<div class="adev-row"><span>Serial</span><b>' +
            esc(d.serial) +
            "</b></div>" +
            '<div class="adev-row"><span>Primary</span><b>' +
            esc(d.primary) +
            "</b></div>" +
            '<div class="adev-row"><span>Status</span><b class="' +
            (d.online ? "adev-val-ok" : "adev-val-muted") +
            '">' +
            (d.online ? "Online" : "Offline") +
            "</b></div>" +
            '<div class="adev-row"><span>Sync pool</span><b>' +
            (d.active ? "Included" : "Not active") +
            "</b></div>" +
            (d.lastSyncError
                ? '<div class="adev-row"><span>Last error</span><b class="adev-val-fail">' +
                  esc(d.lastSyncError) +
                  "</b></div>"
                : "") +
            "</div>";

        const foot = canManageDevices()
            ? '<button type="button" class="adev-btn" data-test="' +
              id +
              '">Test</button><button type="button" class="adev-btn" data-edit="' +
              id +
              '">Edit</button><button type="button" class="adev-btn" data-del="' +
              id +
              '" style="color:var(--adev-red)">Delete</button>'
            : '<button type="button" class="adev-btn" id="adevDetailCloseBtn">Close</button>';

        openModal(d.name, body, foot);
        document.getElementById("adevDetailCloseBtn")?.addEventListener("click", closeSheet);
    }

    async function testDevice(id, name, quiet) {
        const dev = find(id);
        if (dev) {
            dev.testRun = true;
            if (!quiet) render();
        }
        if (!quiet) {
            toast("Testing " + (name || "device") + "…");
            openConnectionTestModal(id, name);
        }

        const ac = new AbortController();
        const timer = setTimeout(function () {
            ac.abort();
        }, TEST_TIMEOUT_MS);

        try {
            const af = token();
            if (!af) {
                if (!quiet) {
                    openConnectionTestModalError(id, name, "Refresh the page and retry (missing anti-forgery token).");
                }
                return;
            }
            const fd = new FormData();
            fd.append("__RequestVerificationToken", af);
            const res = await fetch(base + id, {
                method: "POST",
                body: fd,
                credentials: "same-origin",
                headers: { Accept: "application/json" },
                signal: ac.signal,
            });
            const text = await res.text();
            let data;
            try {
                data = text ? JSON.parse(text) : {};
            } catch {
                if (!quiet) {
                    finishConnectionTestModal({
                        online: false,
                        tcpOk: false,
                        message: "Invalid server response (HTTP " + res.status + ").",
                    });
                }
                return;
            }
            if (!res.ok) {
                if (!quiet) {
                    finishConnectionTestModal({
                        online: false,
                        tcpOk: false,
                        message: data.error || "Test failed",
                    });
                }
                return;
            }
            applyProbeResult(id, data);
            if (!quiet) {
                finishConnectionTestModal(data);
                toast((name || "Device") + (data.online ? " responded" : " not reachable"));
            }
        } catch (e) {
            const msg =
                e && e.name === "AbortError"
                    ? "Test timed out after 3 minutes."
                    : e && e.message
                      ? e.message
                      : "Network error.";
            if (!quiet) {
                if (document.getElementById("adevTestModal")) {
                    finishConnectionTestModal({ online: false, tcpOk: false, message: msg });
                } else {
                    openConnectionTestModalError(id, name, msg);
                }
            }
            toast("Test failed");
        } finally {
            clearTimeout(timer);
            const d = find(id);
            if (d && d.testRun) {
                d.testRun = false;
                if (!quiet) render();
            }
        }
    }

    function confirmDelete(id) {
        const d = find(id);
        if (!d) return;
        openModal(
            "Delete device",
            "<p style=\"margin:0 0 8px;font-weight:600;color:var(--adev-ink)\">Delete " +
                esc(d.name) +
                "?</p>" +
                '<p class="adev-loc" style="font-size:13px;margin:0">It will be removed from the sync pool. This can’t be undone.</p>',
            '<button type="button" class="adev-btn" id="adevDelCancel">Cancel</button>' +
                '<button type="button" class="adev-btn pri" id="adevDelConfirm" style="background:var(--adev-red)">Delete device</button>'
        );
        document.getElementById("adevDelCancel")?.addEventListener("click", closeSheet);
        document.getElementById("adevDelConfirm")?.addEventListener("click", function () {
            postDelete(id);
        });
    }

    async function postDelete(id) {
        const d = find(id);
        if (!d || !d.deleteUrl) return;
        const af = token();
        if (!af) {
            toast("Refresh page and retry");
            return;
        }
        const fd = new FormData();
        fd.append("__RequestVerificationToken", af);
        try {
            const res = await fetch(d.deleteUrl, {
                method: "POST",
                body: fd,
                credentials: "same-origin",
            });
            if (res.ok) {
                devices = devices.filter(function (x) {
                    return x.id !== id;
                });
                closeSheet();
                render();
                toast("Device deleted");
            } else {
                toast("Delete failed");
            }
        } catch {
            toast("Delete failed");
        }
    }

    document.addEventListener("click", function (e) {
        const modalOpen = sheetBg && sheetBg.classList.contains("open");
        if (modalOpen) {
            if (e.target === sheetBg) {
                e.preventDefault();
                closeSheet();
            }
            if (!e.target.closest("#adevSheet")) return;
        }

        const btn = e.target.closest("button");
        if (!btn) return;

        if (btn.id === "adevModalCloseBtn" || btn.id === "adevTestCloseBtn" || btn.id === "adevErrClose" || btn.id === "adevDetailCloseBtn") {
            return;
        }

        if (btn.hasAttribute("data-device-add")) {
            e.preventDefault();
            openDeviceForm();
            return;
        }

        const addCard = e.target.closest(".adev-app.adev-add");
        if (addCard && !e.target.closest("button[data-device-add]")) {
            e.preventDefault();
            openDeviceForm();
            return;
        }

        const a = btn.dataset;
        if (a.open) detail(a.open);
        else if (a.test) {
            const d = find(a.test);
            testDevice(a.test, d?.name);
        } else if (a.edit) openDeviceForm(Number(a.edit));
        else if (a.del) confirmDelete(a.del);
    });

    document.getElementById("adevSearch")?.addEventListener("input", render);

    document.getElementById("adevTestAll")?.addEventListener("click", async function () {
        toast("Testing all devices…");
        for (const d of devices) {
            await testDevice(d.id, d.name, true);
        }
        render();
        toast(
            devices.filter(function (x) {
                return x.online;
            }).length +
                " of " +
                devices.length +
                " responded"
        );
    });

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape") closeSheet();
    });

    function bootGrid() {
        if (!devices.length) {
            hydrateDevicesFromDom();
        }
        const serverTiles = grid && grid.querySelector(".adev-app[data-device-id]");
        if (devices.length && serverTiles) {
            updateStats();
            if (foot) {
                foot.textContent = "Showing " + devices.length + " of " + devices.length + " devices";
            }
        } else {
            render();
        }

        if (grid) {
            setTimeout(function () {
                grid.classList.remove("anim");
            }, 700);
        }

        const params = new URLSearchParams(window.location.search);
        const openId = params.get("open");
        const create = params.get("create");
        if (canManageDevices() && (create === "1" || create === "true")) {
            openDeviceForm();
            params.delete("create");
        } else if (openId && canManageDevices()) {
            openDeviceForm(Number(openId));
            params.delete("open");
        }
        if (create || openId) {
            const qs = params.toString();
            window.history.replaceState({}, "", window.location.pathname + (qs ? "?" + qs : ""));
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", bootGrid);
    } else {
        bootGrid();
    }
})();
