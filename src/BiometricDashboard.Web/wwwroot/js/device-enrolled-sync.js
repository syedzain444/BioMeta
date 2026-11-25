(function () {
    "use strict";

    var lastValidatedRows = [];
    var lastDiffExtraIds = [];
    var activeJobId = null;
    var pollTimer = null;
    var hubConn = null;
    var portalSource = "db";

    function deviceId() {
        var sel = document.getElementById("enrolledDeviceSelect");
        if (sel && sel.value) return parseInt(sel.value, 10);
        var v = document.getElementById("filterDeviceId");
        return v && v.value ? parseInt(v.value, 10) : null;
    }

    function esc(s) {
        return String(s ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

    function rowsFromQueueOrExcel() {
        var rows = [];
        if (window.geuGetImportRows && typeof window.geuGetImportRows === "function") {
            rows = window.geuGetImportRows() || [];
        }
        if (rows.length) return rows;
        var ta = document.getElementById("nicList");
        var text = ta ? String(ta.value || "") : "";
        if (!text.trim()) return [];
        return text
            .split(/\r?\n/)
            .map(function (line, i) {
                return { line: i + 1, employeeId: line.trim() };
            })
            .filter(function (r) {
                return r.employeeId;
            });
    }

    async function postJson(url, body) {
        var res = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
            },
            body: JSON.stringify(body || {}),
        });
        var data = {};
        try {
            data = await res.json();
        } catch (e) {
            data = { ok: false, error: "Invalid JSON response" };
        }
        if (!res.ok && data.ok !== false) data.ok = false;
        return data;
    }

    function mapJobPayload(job) {
        if (!job) return null;
        return {
            state: job.state,
            jobKind: job.jobKind,
            total: job.total,
            uploaded: job.uploaded,
            failed: job.failed,
            verified: job.verified,
            deleted: job.deleted,
            log: job.log,
            error: job.error,
            lines: job.lines || [],
        };
    }

    function renderValidation(data) {
        var summary = document.getElementById("geuValidateSummary");
        var table = document.getElementById("geuValidateTable");
        var body = document.getElementById("geuValidateBody");
        if (!summary || !table || !body) return;
        lastValidatedRows = (data.rows || []).filter(function (r) {
            return r.valid;
        });
        portalSource = lastValidatedRows.length ? "import" : portalSource;
        summary.hidden = false;
        summary.textContent =
            data.validRows +
            " valid · " +
            data.invalidRows +
            " invalid · " +
            data.totalRows +
            " total";
        table.hidden = false;
        body.innerHTML = (data.rows || [])
            .slice(0, 200)
            .map(function (r) {
                var status = r.valid
                    ? '<span class="geu-ok">Valid</span>'
                    : '<span class="geu-bad">' + esc((r.errors || []).join("; ") || "Invalid") + "</span>";
                var warn =
                    r.warnings && r.warnings.length
                        ? '<br><small class="geu-warn-txt">' + esc(r.warnings.join("; ")) + "</small>"
                        : "";
                return (
                    "<tr><td>" +
                    r.line +
                    "</td><td>" +
                    esc(r.employeeId) +
                    "</td><td>" +
                    esc(r.name || "—") +
                    "</td><td>" +
                    status +
                    warn +
                    "</td></tr>"
                );
            })
            .join("");
    }

    function renderDiff(data) {
        var stats = document.getElementById("geuDiffStats");
        var cols = document.getElementById("geuDiffColumns");
        var delBtn = document.getElementById("btnGeuDeleteExtra");
        if (!data.ok) {
            if (window.geuSetStat) window.geuSetStat(data.error || "Compare failed", "warn");
            return;
        }
        if (stats) stats.hidden = false;
        if (cols) cols.hidden = false;
        document.getElementById("geuDiffPortal").textContent = String(data.portalCount);
        document.getElementById("geuDiffDevice").textContent = String(data.deviceCount);
        document.getElementById("geuDiffMatch").textContent = String(data.matchedCount);
        document.getElementById("geuDiffMissing").textContent = String((data.onlyOnPortal || []).length);
        document.getElementById("geuDiffExtra").textContent = String((data.onlyOnDevice || []).length);

        lastDiffExtraIds = (data.onlyOnDevice || []).map(function (x) {
            return x.employeeId;
        });

        if (delBtn) {
            delBtn.hidden = !lastDiffExtraIds.length;
        }

        function li(item) {
            var bio = item.biometricLabel ? " · " + item.biometricLabel : "";
            return (
                "<li><b>" +
                esc(item.employeeId) +
                "</b>" +
                (item.name ? " " + esc(item.name) : "") +
                "<small>" +
                esc(bio) +
                "</small></li>"
            );
        }
        document.getElementById("geuDiffPortalList").innerHTML =
            (data.onlyOnPortal || []).map(li).join("") || "<li class='geu-muted'>—</li>";
        document.getElementById("geuDiffDeviceList").innerHTML =
            (data.onlyOnDevice || []).map(li).join("") || "<li class='geu-muted'>—</li>";
        document.getElementById("geuDiffBothList").innerHTML =
            (data.onBoth || []).slice(0, 150).map(li).join("") || "<li class='geu-muted'>—</li>";
        if (window.geuSetStat) {
            window.geuSetStat(
                "Compare done · " + data.matchedCount + " matched in " + Math.round((data.elapsedMs || 0) / 1000) + "s",
                "ok"
            );
        }
    }

    function updateSyncUi(job) {
        var mapped = mapJobPayload(job);
        if (!mapped) return;
        var wrap = document.getElementById("geuSyncProgress");
        var fill = document.getElementById("geuSyncBarFill");
        var log = document.getElementById("geuSyncLog");
        var lines = document.getElementById("geuSyncLines");
        if (!wrap) return;
        wrap.hidden = false;
        var done = (mapped.uploaded || 0) + (mapped.failed || 0) + (mapped.deleted || 0);
        var pct = mapped.total ? Math.round((done / mapped.total) * 100) : 0;
        if (mapped.state === "Verifying") pct = Math.min(95, pct + 5);
        if (fill) fill.style.width = Math.min(100, pct) + "%";
        if (log) {
            log.textContent =
                (mapped.log || mapped.state) +
                (mapped.verified != null ? " · verified " + mapped.verified : "") +
                (mapped.error ? " · " + mapped.error : "");
        }
        if (lines) {
            lines.innerHTML = (mapped.lines || [])
                .map(function (l) {
                    return (
                        "<tr><td>" +
                        esc(l.employeeId) +
                        "</td><td class='" +
                        (l.succeeded ? "geu-ok" : "geu-bad") +
                        "'>" +
                        (l.succeeded ? "OK" : esc(l.error || "Fail")) +
                        "</td><td class='" +
                        (l.verified ? "geu-ok" : "geu-bad") +
                        "'>" +
                        (l.verified ? "Yes" : "—") +
                        "</td><td>" +
                        esc(l.method || "—") +
                        "</td></tr>"
                    );
                })
                .join("");
        }
        var st = mapped.state;
        if (st === "Completed" || st === "Failed" || st === "Cancelled") {
            stopPoll();
            loadJobHistory();
            if (window.geuSetStat) {
                window.geuSetStat(
                    st === "Completed" ? "Job completed" : "Job " + st.toLowerCase(),
                    st === "Completed" ? "ok" : "warn"
                );
            }
        }
    }

    function stopPoll() {
        if (pollTimer) clearInterval(pollTimer);
        pollTimer = null;
        var cancel = document.getElementById("btnGeuSyncCancel");
        if (cancel) cancel.hidden = true;
        if (hubConn && activeJobId) {
            hubConn.invoke("UnsubscribeJob", activeJobId).catch(function () {});
        }
        activeJobId = null;
    }

    async function ensureHub() {
        if (hubConn || typeof signalR === "undefined") return hubConn;
        hubConn = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/employee-sync")
            .withAutomaticReconnect()
            .build();
        hubConn.on("SyncJobUpdated", function (job) {
            updateSyncUi(job);
        });
        try {
            await hubConn.start();
        } catch (e) {
            hubConn = null;
        }
        return hubConn;
    }

    async function subscribeJob(jobId) {
        activeJobId = jobId;
        var cancel = document.getElementById("btnGeuSyncCancel");
        if (cancel) cancel.hidden = false;
        await ensureHub();
        if (hubConn) {
            try {
                await hubConn.invoke("SubscribeJob", jobId);
            } catch (e) {
                /* fallback poll */
            }
        }
        if (pollTimer) clearInterval(pollTimer);
        pollTimer = setInterval(async function () {
            if (!activeJobId) return;
            try {
                var res = await fetch("/Device/EnrolledUsers/Sync/Status/" + activeJobId, {
                    credentials: "same-origin",
                    headers: { Accept: "application/json" },
                });
                var data = await res.json();
                if (data.ok && data.job) updateSyncUi(data.job);
            } catch (err) {
                /* ignore */
            }
        }, 2500);
    }

    async function loadJobHistory() {
        var devId = deviceId();
        var el = document.getElementById("geuJobHistory");
        if (!el) return;
        try {
            var url =
                "/Device/EnrolledUsers/Sync/History?limit=8" + (devId ? "&deviceId=" + encodeURIComponent(devId) : "");
            var res = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } });
            var data = await res.json();
            if (!data.ok || !data.jobs || !data.jobs.length) return;
            el.hidden = false;
            el.innerHTML = data.jobs
                .map(function (j) {
                    return (
                        "<li><b>" +
                        esc(j.jobKind || "upload") +
                        "</b> " +
                        esc(j.state) +
                        " · " +
                        esc(j.log || "") +
                        "</li>"
                    );
                })
                .join("");
        } catch (e) {
            /* optional */
        }
    }

    async function validateImport() {
        var rows = rowsFromQueueOrExcel();
        var saveReg = document.getElementById("geuSaveRegistry");
        var data = await postJson("/Device/EnrolledUsers/Import/Validate", {
            rows: rows,
            deviceId: deviceId(),
            saveToRegistry: saveReg ? saveReg.checked : false,
        });
        if (!data.ok && !data.rows) {
            if (window.geuSetStat) window.geuSetStat(data.error || "Validation failed", "warn");
            return;
        }
        renderValidation(data);
        if (window.geuSetStat) window.geuSetStat("Import validated" + (saveReg && saveReg.checked ? " · saved to registry" : ""), "ok");
    }

    async function comparePortalDevice() {
        var devId = deviceId();
        if (!devId) {
            if (window.geuSetStat) window.geuSetStat("Select a device first", "warn");
            return;
        }
        var useReg = document.getElementById("geuCompareRegistry");
        if (window.geuSetStat) window.geuSetStat("Comparing portal vs device…", "run");
        var source = useReg && useReg.checked ? "registry" : portalSource === "import" ? "import" : "db";
        var body = {
            deviceId: devId,
            portalSource: source,
            portalIds: lastValidatedRows.map(function (r) {
                return r.employeeId;
            }),
        };
        var data = await postJson("/Device/EnrolledUsers/Compare", body);
        renderDiff(data);
    }

    async function syncToDevice() {
        var devId = deviceId();
        if (!devId) {
            if (window.geuSetStat) window.geuSetStat("Select a device first", "warn");
            return;
        }
        var rows = lastValidatedRows.length ? lastValidatedRows : rowsFromQueueOrExcel();
        if (!rows.length) {
            if (window.geuSetStat) window.geuSetStat("Validate import or paste NICs first", "warn");
            return;
        }
        var saveReg = document.getElementById("geuSaveRegistry");
        if (window.geuSetStat) window.geuSetStat("Starting sync job…", "run");
        var data = await postJson("/Device/EnrolledUsers/Sync/Start", {
            deviceId: devId,
            saveToRegistry: saveReg ? saveReg.checked : true,
            rows: rows.map(function (r) {
                return {
                    line: r.line || 0,
                    employeeId: r.employeeId,
                    name: r.name,
                    department: r.department,
                    cardNumber: r.cardNumber,
                    authority: r.authority,
                    faceDataBase64: r.faceDataBase64,
                };
            }),
        });
        if (!data.ok) {
            if (window.geuSetStat) window.geuSetStat(data.error || "Could not start job", "warn");
            return;
        }
        updateSyncUi({ total: data.total, uploaded: 0, failed: 0, verified: 0, log: "Queued", state: "Queued", lines: [] });
        subscribeJob(data.jobId);
    }

    async function deleteExtraOnDevice() {
        if (!lastDiffExtraIds.length) {
            if (window.geuSetStat) window.geuSetStat("Run compare first — no extra IDs", "warn");
            return;
        }
        if (!window.confirm("Delete " + lastDiffExtraIds.length + " user(s) from the device?")) return;
        var data = await postJson("/Device/EnrolledUsers/Device/DeleteExtra", {
            deviceId: deviceId(),
            employeeIds: lastDiffExtraIds,
        });
        if (!data.ok) {
            if (window.geuSetStat) window.geuSetStat(data.error || "Delete job failed", "warn");
            return;
        }
        subscribeJob(data.jobId);
        updateSyncUi({
            total: data.total,
            deleted: 0,
            failed: 0,
            log: "Deleting extra users…",
            state: "Deleting",
            jobKind: "delete",
            lines: [],
        });
    }

    async function cancelJob() {
        if (!activeJobId) return;
        await postJson("/Device/EnrolledUsers/Sync/Cancel", { jobId: activeJobId });
        stopPoll();
        if (window.geuSetStat) window.geuSetStat("Sync job cancelled", "warn");
    }

    function bind() {
        if (!document.body.classList.contains("gateos-page-enrolled")) return;
        document.getElementById("btnGeuValidateImport")?.addEventListener("click", validateImport);
        document.getElementById("btnGeuCompare")?.addEventListener("click", comparePortalDevice);
        document.getElementById("btnGeuSyncStart")?.addEventListener("click", syncToDevice);
        document.getElementById("btnGeuDeleteExtra")?.addEventListener("click", deleteExtraOnDevice);
        document.getElementById("btnGeuSyncCancel")?.addEventListener("click", cancelJob);
        loadJobHistory();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", bind);
    } else {
        bind();
    }
})();