(function () {
    "use strict";

    var rows = [];
    var fetching = false;
    var fetchAbort = null;
    var progressTimer = null;

    function selectedDeviceIds() {
        var ids = [];
        document.querySelectorAll(".deh-dev-chk:checked").forEach(function (el) {
            var v = parseInt(el.value, 10);
            if (v > 0) ids.push(v);
        });
        return ids;
    }

    function allActiveDeviceIds() {
        var ids = [];
        document.querySelectorAll(".deh-dev-chk").forEach(function (el) {
            var v = parseInt(el.value, 10);
            if (v > 0) ids.push(v);
        });
        return ids;
    }

    function deviceLabel(deviceId) {
        var el = document.querySelector('.deh-dev-chk[value="' + deviceId + '"]');
        if (!el) return "Device " + deviceId;
        return el.getAttribute("data-name") || "Device " + deviceId;
    }

    function setStat(text, kind) {
        var el = document.getElementById("dehStat");
        if (!el) return;
        el.textContent = text || "";
        el.style.color = kind === "warn" ? "#f5a623" : kind === "err" ? "#f56565" : "";
    }

    function setBusy(busy) {
        fetching = busy;
        ["dehFetchSel", "dehFetchAll", "dehLoadStored"].forEach(function (id) {
            var b = document.getElementById(id);
            if (b) b.disabled = busy;
        });
        var cancel = document.getElementById("dehCancel");
        if (cancel) cancel.hidden = !busy;
    }

    function stopProgressTimer() {
        if (progressTimer) {
            clearInterval(progressTimer);
            progressTimer = null;
        }
    }

    function startProgressTimer(prefix) {
        stopProgressTimer();
        var t0 = Date.now();
        progressTimer = setInterval(function () {
            var sec = Math.floor((Date.now() - t0) / 1000);
            setStat(
                prefix +
                    " · " +
                    sec +
                    "s (up to ~12 min/device; Browser Link console errors are harmless)"
            );
        }, 1000);
    }

    function abortFetch() {
        if (fetchAbort) {
            fetchAbort.abort();
            fetchAbort = null;
        }
        stopProgressTimer();
    }

    function enableActions() {
        var has = rows.length > 0;
        var cmp = document.getElementById("dehCompare");
        var save = document.getElementById("dehSave");
        var opts = document.getElementById("dehSaveOpts");
        if (cmp) cmp.disabled = !has || fetching;
        if (save) save.disabled = !has || fetching;
        if (opts) opts.hidden = !has;
    }

    function renderMainTable() {
        var tbody = document.getElementById("dehTbody");
        if (!tbody) return;
        tbody.innerHTML = "";
        if (!rows.length) {
            var empty = document.createElement("tr");
            empty.id = "dehEmpty";
            empty.innerHTML =
                '<td colspan="7" class="deh-empty">No rows yet — tick device(s) and click <strong>Fetch selected</strong> (each device may take several minutes).</td>';
            tbody.appendChild(empty);
            return;
        }
        rows.forEach(function (r) {
            var tr = document.createElement("tr");
            tr.innerHTML =
                "<td>" +
                (r.deviceName || "") +
                "<br><small>" +
                (r.deviceIp || "") +
                "</small></td><td>" +
                (r.employeeId || "") +
                "</td><td>" +
                (r.name || "") +
                "</td><td>" +
                (r.department || "—") +
                "</td><td>" +
                (r.cardNumber || "—") +
                "</td><td>" +
                (r.biometricLabel || r.biometricStatus || "—") +
                "</td><td>" +
                (r.enrolledAt || "—") +
                "</td>";
            tbody.appendChild(tr);
        });
    }

    async function readApiJson(res) {
        var text = await res.text();
        if (!text) return { ok: false, error: "Empty response (HTTP " + res.status + ")" };
        try {
            return JSON.parse(text);
        } catch (e) {
            return {
                ok: false,
                error:
                    "Server error (HTTP " +
                    res.status +
                    "). " +
                    (res.status === 504 ? "Request timed out — try fewer devices at once." : ""),
            };
        }
    }

    async function postJson(url, body, signal) {
        var res = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", Accept: "application/json" },
            body: JSON.stringify(body || {}),
            signal: signal,
        });
        return readApiJson(res);
    }

    async function fetchOneDevice(deviceId, index, total, signal) {
        var label = deviceLabel(deviceId);
        var prefix = "Fetching " + (index + 1) + "/" + total + ": " + label;
        startProgressTimer(prefix);
        var data;
        try {
            data = await postJson(
                "/Device/EnrollmentHub/FetchDevice",
                { deviceId: deviceId, delayMs: 120, pushWaitSeconds: 120 },
                signal
            );
        } catch (e) {
            if (e && e.name === "AbortError") return { rows: [], warning: label + ": cancelled", aborted: true };
            throw e;
        } finally {
            stopProgressTimer();
        }
        if (!data.ok && !(data.rows && data.rows.length)) {
            return { rows: [], warning: data.error || label + ": fetch failed" };
        }
        var warn =
            data.warnings && data.warnings.length
                ? data.warnings.join(" ")
                : data.error && data.rows && data.rows.length
                  ? data.error
                  : null;
        if (!data.rows || !data.rows.length)
            return { rows: [], warning: data.error || label + ": no users" };
        return { rows: data.rows, warning: warn, found: data.found, elapsedMs: data.elapsedMs };
    }

    async function fetchDevices(allActive, ids) {
        if (fetching) return;
        var targetIds = allActive ? allActiveDeviceIds() : ids;
        if (!targetIds.length) {
            setStat("Select at least one device", "warn");
            return;
        }
        setBusy(true);
        abortFetch();
        fetchAbort = new AbortController();
        var signal = fetchAbort.signal;
        rows = [];
        renderMainTable();
        var warnings = [];
        var totalElapsed = 0;
        try {
            for (var i = 0; i < targetIds.length; i++) {
                if (signal.aborted) break;
                var part = await fetchOneDevice(targetIds[i], i, targetIds.length, signal);
                if (part.aborted) {
                    setStat("Fetch cancelled", "warn");
                    break;
                }
                if (part.rows.length) rows = rows.concat(part.rows);
                if (part.warning) warnings.push(part.warning);
                if (part.elapsedMs) totalElapsed += part.elapsedMs;
                renderMainTable();
            }
            enableActions();
            document.getElementById("dehComparePanel").hidden = true;
            if (!rows.length) {
                setStat(warnings[0] || "No users returned from selected devices", "err");
                if (warnings.length) alert(warnings.join("\n\n"));
                return;
            }
            var hint = rows.length + " row(s) · " + targetIds.length + " device(s)";
            if (totalElapsed) hint += " · ~" + Math.round(totalElapsed / 1000) + "s";
            if (warnings.length) {
                setStat(hint + " (see warnings)", "warn");
                console.warn(warnings);
                alert(warnings.join("\n\n"));
            } else setStat(hint, "ok");
        } catch (e) {
            setStat("Network error: " + (e.message || "fetch failed"), "err");
        } finally {
            abortFetch();
            setBusy(false);
            enableActions();
        }
    }

    async function loadStored() {
        if (fetching) return;
        var ids = selectedDeviceIds();
        if (!ids.length) {
            setStat("Select devices", "warn");
            return;
        }
        setBusy(true);
        setStat("Loading from portal DB…");
        try {
            var res = await fetch(
                "/Device/EnrollmentHub/Stored?deviceIds=" + encodeURIComponent(ids.join(",")),
                { credentials: "same-origin", headers: { Accept: "application/json" } }
            );
            var data = await readApiJson(res);
            if (!data.ok) {
                setStat(data.error || "Load failed", "err");
                return;
            }
            rows = (data.rows || []).map(function (r) {
                return {
                    deviceId: r.deviceId,
                    deviceName: r.deviceName,
                    deviceIp: r.deviceIp,
                    employeeId: r.employeeId,
                    name: r.name,
                    department: r.department,
                    cardNumber: r.cardNumber,
                    biometricStatus: r.biometricStatus,
                    enrolledAt: r.enrolledAt,
                    enrolledAtSource: r.enrolledAtSource,
                };
            });
            renderMainTable();
            enableActions();
            if (!rows.length) {
                setStat(
                    "No saved rows in portal — fetch from device first, then Save in portal (run 013 SQL if needed).",
                    "warn"
                );
            } else setStat(rows.length + " row(s) from portal DB", "ok");
        } catch (e) {
            setStat("Load failed: " + (e.message || "network"), "err");
        } finally {
            setBusy(false);
            enableActions();
        }
    }

    function renderCompare(c) {
        document.getElementById("dehComparePanel").hidden = false;
        var stats = document.getElementById("dehCompareStats");
        if (stats) {
            stats.textContent =
                "Total rows: " +
                c.totalRows +
                " · Distinct IDs: " +
                c.distinctEmployeeIds +
                " · On multiple devices: " +
                c.onMultipleDevices +
                " · Name conflicts: " +
                c.nameConflicts;
        }
        var tbS = document.querySelector("#dehTblShared tbody");
        var tbU = document.querySelector("#dehTblUnique tbody");
        if (tbS) {
            tbS.innerHTML = "";
            (c.sharedAcrossDevices || []).forEach(function (g) {
                var tr = document.createElement("tr");
                var devs = (g.devices || [])
                    .map(function (d) {
                        return (d.deviceName || d.bioDeviceId) + (d.displayName ? " (" + d.displayName + ")" : "");
                    })
                    .join("; ");
                tr.innerHTML =
                    "<td>" +
                    g.employeeId +
                    "</td><td>" +
                    devs +
                    "</td><td>" +
                    (g.nameMismatch ? "Yes" : "—") +
                    "</td>";
                tbS.appendChild(tr);
            });
        }
        if (tbU) {
            tbU.innerHTML = "";
            (c.uniquePerDevice || []).forEach(function (s) {
                var sample = (s.employeeIds || []).slice(0, 8).join(", ");
                if ((s.employeeIds || []).length > 8) sample += "…";
                var tr = document.createElement("tr");
                tr.innerHTML =
                    "<td>" +
                    (s.deviceName || s.bioDeviceId) +
                    "</td><td>" +
                    s.count +
                    "</td><td><small>" +
                    sample +
                    "</small></td>";
                tbU.appendChild(tr);
            });
        }
    }

    async function compareData() {
        if (!rows.length) return;
        setStat("Comparing…");
        var payload = rows.map(function (r) {
            return {
                deviceId: r.deviceId,
                deviceName: r.deviceName,
                employeeId: r.employeeId,
                name: r.name,
                department: r.department,
                cardNumber: r.cardNumber,
            };
        });
        var data = await postJson("/Device/EnrollmentHub/Compare", { rows: payload });
        if (!data.ok) {
            setStat(data.error || "Compare failed", "err");
            return;
        }
        renderCompare(data.compare);
        setStat("Comparison ready", "ok");
    }

    async function savePortal() {
        if (!rows.length) return;
        var also = document.getElementById("dehAlsoRegistry");
        setStat("Saving…");
        var data = await postJson("/Device/EnrollmentHub/SavePortal", {
            rows: rows,
            alsoUpdateRegistry: also && also.checked,
            note: "enrollment-hub UI",
        });
        if (!data.ok) {
            setStat(data.error || data.message || "Save failed", "err");
            alert(data.message || data.error || "Save failed");
            return;
        }
        setStat(data.message || "Saved", "ok");
        alert(data.message);
    }

    function init() {
        renderMainTable();
        setStat("Ready — select device(s), then Fetch selected");

        var chkAll = document.getElementById("dehChkAll");
        if (chkAll) {
            chkAll.addEventListener("change", function () {
                document.querySelectorAll(".deh-dev-chk").forEach(function (el) {
                    el.checked = chkAll.checked;
                });
            });
            var allOn = document.querySelectorAll(".deh-dev-chk").length;
            var checked = document.querySelectorAll(".deh-dev-chk:checked").length;
            if (allOn > 0 && checked === allOn) chkAll.checked = true;
        }
        var fetchSel = document.getElementById("dehFetchSel");
        if (fetchSel) {
            fetchSel.addEventListener("click", function () {
                fetchDevices(false, selectedDeviceIds());
            });
        }
        var fetchAll = document.getElementById("dehFetchAll");
        if (fetchAll) fetchAll.addEventListener("click", function () { fetchDevices(true, []); });
        var loadSt = document.getElementById("dehLoadStored");
        if (loadSt) loadSt.addEventListener("click", loadStored);
        var cmp = document.getElementById("dehCompare");
        if (cmp) cmp.addEventListener("click", compareData);
        var save = document.getElementById("dehSave");
        if (save) save.addEventListener("click", savePortal);
        var cancel = document.getElementById("dehCancel");
        if (cancel) {
            cancel.addEventListener("click", function () {
                abortFetch();
                setStat("Cancelling…", "warn");
            });
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else init();
})();