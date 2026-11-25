(function () {
    "use strict";

    const COL = {
        time: 0,
        empId: 1,
        name: 2,
        device: 3,
        location: 4,
        devId: 5,
        status: 6,
        work: 7,
        source: 8,
        serial: 9,
    };

    let portalTable = null;
    let deviceTable = null;
    let liveTable = null;
    let liveStarted = false;
    let liveKeys = new Set();

    const pushMode = String(document.getElementById("logsSyncMode")?.value || "").toLowerCase() === "push";
    const DEVICE_CHUNK_DAYS = 7;

    function pad2(n) {
        return String(n).padStart(2, "0");
    }

    function parseYmd(str) {
        if (!str) return null;
        const p = str.split("-").map(Number);
        if (p.length < 3 || !p[0]) return null;
        return new Date(p[0], p[1] - 1, p[2]);
    }

    function formatYmd(d) {
        return d.getFullYear() + "-" + pad2(d.getMonth() + 1) + "-" + pad2(d.getDate());
    }

    function monthValueFromDate(d) {
        return d.getFullYear() + "-" + pad2(d.getMonth() + 1);
    }

    function boundsFromMonthValue(ym) {
        if (!ym || ym.indexOf("-") < 0) return null;
        const parts = ym.split("-").map(Number);
        const y = parts[0];
        const m = parts[1];
        if (!y || !m) return null;
        const from = new Date(y, m - 1, 1);
        const to = new Date(y, m, 0);
        return { from: formatYmd(from), to: formatYmd(to) };
    }

    function applyMonthToDates(ym) {
        const b = boundsFromMonthValue(ym);
        if (!b) return false;
        $("#fromDate").val(b.from);
        $("#toDate").val(b.to);
        return true;
    }

    function syncMonthInputFromDates() {
        const from = parseYmd($("#fromDate").val());
        const to = parseYmd($("#toDate").val());
        const el = document.getElementById("filterMonth");
        if (!el || !from || !to) return;
        if (from.getDate() === 1) {
            const last = new Date(from.getFullYear(), from.getMonth() + 1, 0);
            if (formatYmd(to) === formatYmd(last)) {
                el.value = monthValueFromDate(from);
                return;
            }
        }
    }

    function setMonthPreset(preset) {
        const now = new Date();
        let y = now.getFullYear();
        let m = now.getMonth();
        if (preset === "last") {
            m -= 1;
            if (m < 0) {
                m = 11;
                y -= 1;
            }
        }
        const ym = y + "-" + pad2(m + 1);
        const el = document.getElementById("filterMonth");
        if (el) el.value = ym;
        applyMonthToDates(ym);
    }

    function chunkDateRanges(fromStr, toStr, chunkDays) {
        const start = parseYmd(fromStr);
        const end = parseYmd(toStr);
        if (!start || !end || start > end) {
            return [{ fromDate: fromStr, toDate: toStr }];
        }
        const chunks = [];
        let cur = new Date(start.getTime());
        while (cur <= end) {
            const chunkEnd = new Date(cur.getTime());
            chunkEnd.setDate(chunkEnd.getDate() + chunkDays - 1);
            if (chunkEnd > end) chunkEnd.setTime(end.getTime());
            chunks.push({ fromDate: formatYmd(cur), toDate: formatYmd(chunkEnd) });
            cur = new Date(chunkEnd.getTime());
            cur.setDate(cur.getDate() + 1);
        }
        return chunks;
    }

    function deviceRowKey(row) {
        return (row[COL.empId] || "") + "|" + (row[COL.time] || "");
    }

    function filterParams() {
        const p = {
            fromDate: $("#fromDate").val(),
            toDate: $("#toDate").val(),
            fromTime: $("#fromTime").val(),
            toTime: $("#toTime").val(),
        };
        const deviceId = $("#filterDeviceId").val();
        if (deviceId) p.deviceId = deviceId;
        return p;
    }

    async function fetchJson(url) {
        const res = await fetch(url, { credentials: "same-origin" });
        const text = await res.text();
        if (!text || !text.trim()) {
            const hint =
                res.status === 404
                    ? "Endpoint not found — restart the portal after update."
                    : res.status === 504 || res.status === 408
                      ? "Server timed out (device may be busy or offline)."
                      : "Empty response (HTTP " + res.status + ").";
            throw new Error(hint);
        }
        let json;
        try {
            json = JSON.parse(text);
        } catch {
            throw new Error("Server returned non-JSON (HTTP " + res.status + ").");
        }
        if (!res.ok && json && json.error) throw new Error(json.error);
        if (!res.ok) throw new Error("HTTP " + res.status);
        return json;
    }

    function empIdRender(data) {
        if (!data) return "—";
        return '<code class="emp-id">' + $("<div>").text(data).html() + "</code>";
    }

    function baseColumnDefs(offset) {
        offset = offset || 0;
        return [
            {
                targets: offset + COL.empId,
                render: empIdRender,
            },
        ];
    }

    function initPortalTable() {
        const el = document.getElementById("logsTablePortal");
        if (!el || portalTable) return;
        portalTable = PortalDataTable.init(el, {
            processing: true,
            serverSide: true,
            order: [[COL.time, "desc"]],
            pageLength: 25,
            ajax: {
                url: "/Logs/Data",
                data: function (d) {
                    Object.assign(d, filterParams());
                },
            },
            columns: Array.from({ length: 10 }, (_, i) => ({ data: i })),
            columnDefs: baseColumnDefs(0),
        });
        requestAnimationFrame(function () {
            PortalDataTable.relayout(el);
        });
    }

    function initDeviceTable() {
        const el = document.getElementById("logsTableDevice");
        if (!el || deviceTable) return;
        deviceTable = PortalDataTable.init(el, {
            order: [[COL.time, "desc"]],
            pageLength: 50,
            data: [],
            columns: Array.from({ length: 10 }, (_, i) => ({ data: i })),
            columnDefs: baseColumnDefs(0),
            language: {
                emptyTable: "Click “Load from device” to pull GetRecord for the date range above.",
            },
        });
    }

    function initLiveTable() {
        const el = document.getElementById("logsTableLive");
        if (!el || liveTable) return;
        liveTable = PortalDataTable.init(el, {
            order: [[1, "desc"]],
            pageLength: 50,
            data: [],
            columns: [
                {
                    data: null,
                    defaultContent: "",
                    orderable: false,
                    searchable: false,
                    render: function () {
                        return '<span class="row-live-dot"></span>';
                    },
                },
                { data: 0 },
                { data: 1 },
                { data: 2 },
                { data: 3 },
                { data: 4 },
                { data: 5 },
                { data: 6 },
                { data: 7 },
                { data: 8 },
            ],
            columnDefs: [{ targets: 2, render: empIdRender }],
            language: {
                emptyTable: "Waiting for punches — live feed will fill as records arrive.",
            },
        });
    }

    function punchToLiveRow(p) {
        return [
            p.punchTime || p.PunchTime || "",
            p.employeeId || p.EmployeeId || "",
            p.employeeName || p.EmployeeName || "",
            p.deviceName || p.DeviceName || "",
            p.deviceLocation || p.DeviceLocation || "",
            p.deviceId != null ? String(p.deviceId) : p.BioDeviceId != null ? String(p.BioDeviceId) : "",
            p.status || p.Status || "",
            p.workCode || p.WorkCode || "",
            p.cardSource || p.CardSource || "",
        ];
    }

    function liveKeyFromRow(row) {
        return (row[1] || "") + "|" + (row[0] || "");
    }

    function appendLiveRows(punches) {
        if (!liveTable || !Array.isArray(punches)) return;
        const toAdd = [];
        punches.forEach(function (p) {
            const row = punchToLiveRow(p);
            const key = liveKeyFromRow(row);
            if (liveKeys.has(key)) return;
            liveKeys.add(key);
            toAdd.push(row);
        });
        if (toAdd.length) {
            liveTable.rows.add(toAdd).draw(false);
        }
    }

    function setLiveStatus(text, online) {
        const pill = document.getElementById("liveFeedPill");
        const dot = document.getElementById("logsLiveDot");
        if (pill) {
            pill.textContent = text;
            pill.classList.toggle("is-on", !!online);
        }
        if (dot) dot.classList.toggle("is-on", !!online);
    }

    async function refreshLiveFromServer() {
        const p = filterParams();
        const qs = new URLSearchParams({ count: "200" });
        if (p.deviceId) qs.set("deviceId", p.deviceId);
        try {
            const json = await fetchJson("/Logs/LiveRecent?" + qs.toString());
            if (!json.ok || !liveTable) return;
            liveTable.clear();
            liveKeys.clear();
            const rows = json.data || [];
            rows.forEach(function (row) {
                liveKeys.add(liveKeyFromRow(row));
            });
            liveTable.rows.add(rows).draw(false);
        } catch {
            setLiveStatus("Could not load recent rows", false);
        }
    }

    function startLiveFeed() {
        if (liveStarted) return;
        liveStarted = true;
        initLiveTable();
        refreshLiveFromServer();

        if (typeof signalR === "undefined") {
            setLiveStatus("SignalR unavailable", false);
            return;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/attendance", { withCredentials: true })
            .withAutomaticReconnect()
            .build();

        connection.on("NewPunches", function (payload) {
            if (Array.isArray(payload) && payload.length) {
                appendLiveRows(payload);
                setLiveStatus("Live · " + payload.length + " new", true);
            } else {
                refreshLiveFromServer();
                setLiveStatus("Live · updated", true);
            }
        });

        connection
            .start()
            .then(function () {
                setLiveStatus(pushMode ? "Live · push" : "Live · sync", true);
            })
            .catch(function () {
                setLiveStatus("SignalR offline — showing DB recent only", false);
            });

        setInterval(function () {
            if (document.visibilityState === "visible" && document.querySelector('[data-logs-panel="live"]:not([hidden])')) {
                refreshLiveFromServer();
            }
        }, pushMode ? 5000 : 15000);
    }

    function swalDeviceLoading(title, html) {
        if (typeof Swal === "undefined") return;
        Swal.fire({
            title: title || "Loading from device",
            html: html || "GetRecord on HDCP port <strong>9922</strong> — up to 2 minutes per chunk.",
            allowOutsideClick: false,
            allowEscapeKey: false,
            showConfirmButton: false,
            didOpen: function () {
                Swal.showLoading();
            },
        });
    }

    function swalDeviceProgress(title, html) {
        if (typeof Swal === "undefined") return;
        if (Swal.isVisible()) {
            Swal.update({ title: title, html: html });
        } else {
            swalDeviceLoading(title, html);
        }
    }

    function swalDeviceClose() {
        if (typeof Swal !== "undefined" && Swal.isVisible()) Swal.close();
    }

    function swalDeviceSuccess(json, meta) {
        if (typeof Swal === "undefined") return;
        meta = meta || {};
        let extra =
            json.totalOnDevice != null
                ? "<br><small>" + json.totalOnDevice + " total stored on device</small>"
                : "";
        if (meta.chunks > 1) {
            extra +=
                "<br><small>Date range split into <strong>" +
                meta.chunks +
                "</strong> weekly GetRecord request(s)</small>";
        }
        if (meta.fromDate && meta.toDate) {
            extra += "<br><small>" + meta.fromDate + " → " + meta.toDate + "</small>";
        }
        Swal.fire({
            icon: "success",
            title: "Device memory loaded",
            html:
                "<strong>" +
                (json.loaded || 0) +
                "</strong> row(s) from <strong>" +
                (json.deviceName || "device") +
                "</strong> (" +
                (json.deviceIp || "—") +
                ")" +
                extra,
            confirmButtonText: "OK",
            confirmButtonColor: "#059669",
        });
    }

    function swalDeviceError(message) {
        if (typeof Swal === "undefined") {
            window.alert(message);
            return;
        }
        Swal.fire({
            icon: "error",
            title: "Could not load device memory",
            text: message,
            confirmButtonText: "OK",
            confirmButtonColor: "#0f766e",
        });
    }

    async function fetchDeviceRange(params) {
        const qs = new URLSearchParams(params);
        return fetchJson("/Logs/DeviceData?" + qs.toString());
    }

    async function loadDeviceLogs() {
        initDeviceTable();
        const btn = document.getElementById("btnLoadDevice");
        const fp = filterParams();
        if (!fp.fromDate || !fp.toDate) {
            swalDeviceError("Set From date and To date (or pick a month and Apply month to dates).");
            return;
        }
        const chunks = chunkDateRanges(fp.fromDate, fp.toDate, DEVICE_CHUNK_DAYS);
        if (btn) btn.disabled = true;
        swalDeviceLoading(
            "Loading from device",
            chunks.length > 1
                ? "Month/range will use <strong>" +
                  chunks.length +
                  "</strong> GetRecord chunk(s) (7 days each) on port <strong>9922</strong>."
                : undefined
        );
        try {
            const merged = [];
            const seen = new Set();
            let lastOk = null;
            let failedChunk = null;

            for (let i = 0; i < chunks.length; i++) {
                const c = chunks[i];
                swalDeviceProgress(
                    "Loading from device",
                    "Chunk <strong>" +
                        (i + 1) +
                        "</strong> of <strong>" +
                        chunks.length +
                        "</strong><br><small>" +
                        c.fromDate +
                        " → " +
                        c.toDate +
                        "</small>"
                );
                const params = Object.assign({}, fp, {
                    fromDate: c.fromDate,
                    toDate: c.toDate,
                });
                const json = await fetchDeviceRange(params);
                if (!json.ok) {
                    failedChunk = { index: i + 1, error: json.error, chunk: c };
                    break;
                }
                lastOk = json;
                (json.data || []).forEach(function (row) {
                    const key = deviceRowKey(row);
                    if (seen.has(key)) return;
                    seen.add(key);
                    merged.push(row);
                });
            }

            swalDeviceClose();

            if (failedChunk && merged.length === 0) {
                swalDeviceError(
                    (failedChunk.error || "GetRecord failed") +
                        " (chunk " +
                        failedChunk.index +
                        ": " +
                        failedChunk.chunk.fromDate +
                        " → " +
                        failedChunk.chunk.toDate +
                        ")"
                );
                return;
            }

            merged.sort(function (a, b) {
                return String(b[COL.time]).localeCompare(String(a[COL.time]));
            });

            deviceTable.clear();
            deviceTable.rows.add(merged).draw();
            PortalDataTable.relayout(document.getElementById("logsTableDevice"));

            const summary = {
                ok: true,
                loaded: merged.length,
                deviceName: lastOk?.deviceName,
                deviceIp: lastOk?.deviceIp,
                totalOnDevice: lastOk?.totalOnDevice,
            };

            if (failedChunk) {
                Swal.fire({
                    icon: "warning",
                    title: "Partial load",
                    html:
                        "Loaded <strong>" +
                        merged.length +
                        "</strong> row(s) before chunk " +
                        failedChunk.index +
                        " failed.<br><small>" +
                        (failedChunk.error || "GetRecord failed") +
                        "</small>",
                    confirmButtonColor: "#059669",
                });
            } else {
                swalDeviceSuccess(summary, {
                    chunks: chunks.length,
                    fromDate: fp.fromDate,
                    toDate: fp.toDate,
                });
            }
        } catch (e) {
            swalDeviceClose();
            swalDeviceError(e.message || "Network error");
        } finally {
            if (btn) btn.disabled = false;
        }
    }

    function activeTabName() {
        return document.querySelector(".logs-tab.is-active")?.getAttribute("data-logs-tab") || "portal";
    }

    function syncSidebarActions(tab) {
        document.querySelectorAll(".logs-sidebar-tab-only").forEach(function (el) {
            const forTab = el.getAttribute("data-for-tab");
            el.hidden = forTab !== tab;
        });
    }

    function cellPlain(value) {
        if (value == null) return "";
        const s = String(value);
        if (s.indexOf("<") >= 0) {
            const d = document.createElement("div");
            d.innerHTML = s;
            return (d.textContent || d.innerText || "").trim();
        }
        return s;
    }

    function tableHeaders(tableEl, skipIndexes) {
        skipIndexes = skipIndexes || [];
        const headers = [];
        tableEl.querySelectorAll("thead th").forEach(function (th, i) {
            if (skipIndexes.indexOf(i) >= 0) return;
            headers.push(th.textContent.trim());
        });
        return headers;
    }

    function rowsFromDataTable(table, skipIndexes) {
        skipIndexes = skipIndexes || [];
        const aoa = [];
        table.rows({ search: "applied" }).every(function () {
            const row = this.data();
            const cells = Array.isArray(row) ? row : [];
            const out = [];
            cells.forEach(function (cell, i) {
                if (skipIndexes.indexOf(i) >= 0) return;
                out.push(cellPlain(cell));
            });
            if (out.length) aoa.push(out);
        });
        return aoa;
    }

    function writeExcel(aoa, fileBase) {
        if (!window.XLSX) {
            swalDeviceError("Excel library not loaded. Refresh the page.");
            return;
        }
        if (!aoa.length || aoa.length < 2) {
            if (typeof Swal !== "undefined") {
                Swal.fire({
                    icon: "info",
                    title: "Nothing to export",
                    text: "No rows in the current tab (apply filters or load data first).",
                    confirmButtonColor: "#059669",
                });
            }
            return;
        }
        const ws = XLSX.utils.aoa_to_sheet(aoa);
        const wb = XLSX.utils.book_new();
        XLSX.utils.book_append_sheet(wb, ws, "Logs");
        XLSX.writeFile(wb, fileBase + ".xlsx");
    }

    async function exportPortalExcel() {
        const tableEl = document.getElementById("logsTablePortal");
        const headers = tableHeaders(tableEl);
        const qs = new URLSearchParams({
            draw: "1",
            start: "0",
            length: "100000",
            "search[value]": portalTable ? portalTable.search() : "",
        });
        const fp = filterParams();
        Object.keys(fp).forEach(function (k) {
            if (fp[k]) qs.set(k, fp[k]);
        });
        if (typeof Swal !== "undefined") {
            Swal.fire({
                title: "Preparing export",
                allowOutsideClick: false,
                showConfirmButton: false,
                didOpen: function () {
                    Swal.showLoading();
                },
            });
        }
        try {
            const json = await fetchJson("/Logs/Data?" + qs.toString());
            if (typeof Swal !== "undefined") Swal.close();
            const rows = (json.data || []).map(function (r) {
                return r.map(cellPlain);
            });
            writeExcel([headers].concat(rows), "attendance-portal-db");
        } catch (e) {
            if (typeof Swal !== "undefined") Swal.close();
            swalDeviceError(e.message || "Export failed");
        }
    }

    function exportDeviceExcel() {
        if (!deviceTable) {
            swalDeviceError("Load device data first.");
            return;
        }
        const tableEl = document.getElementById("logsTableDevice");
        const aoa = [tableHeaders(tableEl)].concat(rowsFromDataTable(deviceTable));
        writeExcel(aoa, "attendance-device-memory");
    }

    function exportLiveExcel() {
        if (!liveTable) initLiveTable();
        const tableEl = document.getElementById("logsTableLive");
        const aoa = [tableHeaders(tableEl, [0])].concat(rowsFromDataTable(liveTable));
        writeExcel(aoa, "attendance-live");
    }

    async function exportActiveTabExcel() {
        const tab = activeTabName();
        if (tab === "portal") return exportPortalExcel();
        if (tab === "device") return exportDeviceExcel();
        return exportLiveExcel();
    }

    function switchTab(name) {
        document.querySelectorAll(".logs-tab").forEach(function (btn) {
            const on = btn.getAttribute("data-logs-tab") === name;
            btn.classList.toggle("is-active", on);
            btn.setAttribute("aria-selected", on ? "true" : "false");
        });
        document.querySelectorAll(".logs-panel").forEach(function (panel) {
            const on = panel.getAttribute("data-logs-panel") === name;
            panel.classList.toggle("is-active", on);
            panel.hidden = !on;
        });
        syncSidebarActions(name);
        if (name === "live") startLiveFeed();
        if (name === "device") initDeviceTable();
        if (name === "portal" && portalTable) {
            PortalDataTable.relayout(document.getElementById("logsTablePortal"));
            portalTable.columns.adjust().draw(false);
        }
        if (name === "device" && deviceTable) {
            PortalDataTable.relayout(document.getElementById("logsTableDevice"));
            deviceTable.columns.adjust().draw(false);
        }
        if (name === "live" && liveTable) {
            PortalDataTable.relayout(document.getElementById("logsTableLive"));
            liveTable.columns.adjust().draw(false);
        }
    }

    function applyFilters() {
        const tab = activeTabName();
        if (tab === "portal" && portalTable) portalTable.ajax.reload();
        if (tab === "device" && deviceTable && deviceTable.rows().count() > 0) loadDeviceLogs();
        if (tab === "live") refreshLiveFromServer();
    }

    $(function () {
        initPortalTable();

        document.querySelectorAll(".logs-tab").forEach(function (btn) {
            btn.addEventListener("click", function () {
                switchTab(btn.getAttribute("data-logs-tab"));
            });
        });

        $("#btnApply").on("click", applyFilters);
        $("#btnClear").on("click", function () {
            $("#fromDate, #toDate, #fromTime, #toTime, #filterMonth").val("");
            applyFilters();
        });
        $("#btnApplyMonth").on("click", function () {
            const ym = $("#filterMonth").val();
            if (!applyMonthToDates(ym)) {
                swalDeviceError("Pick a valid month first.");
                return;
            }
            applyFilters();
        });
        $(".logs-month-chip").on("click", function () {
            setMonthPreset($(this).data("month-preset"));
            syncMonthInputFromDates();
        });
        $("#filterMonth").on("change", function () {
            applyMonthToDates($(this).val());
        });
        $("#fromDate, #toDate").on("change", syncMonthInputFromDates);
        $("#btnLoadDevice").on("click", loadDeviceLogs);
        $("#btnExportExcel").on("click", function () {
            exportActiveTabExcel();
        });

        syncSidebarActions(activeTabName());

        const today = new Date();
        const monthAgo = new Date(today);
        monthAgo.setDate(monthAgo.getDate() - 30);
        if (!$("#fromDate").val()) {
            setMonthPreset("this");
        } else {
            syncMonthInputFromDates();
        }
    });
})();
