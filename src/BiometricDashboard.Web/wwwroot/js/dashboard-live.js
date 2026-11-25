(function () {
    const livePill = document.getElementById("livePill");
    const liveLabel = document.getElementById("liveLabel");
    const searchBox = document.getElementById("searchBox");
    const filterToday = document.getElementById("filterToday");
    const statSynced = document.getElementById("statSynced");
    const tableEl = document.getElementById("punchGrid");

    const pushMode = String(window.syncMode || "").toLowerCase() === "push";
    const pollMs = Math.max(2000, (window.dashboardPollSeconds || 3) * 1000);
    let syncTick = 0;
    let syncInFlight = false;
    let todayOnly = true;
    let punchTable = null;

    function todayIso() {
        const d = new Date();
        return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    }

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function initDataTable() {
        if (!tableEl || !window.PortalDataTable || !window.jQuery) return null;

        const $ = window.jQuery;
        if ($.fn.DataTable.isDataTable(tableEl)) {
            punchTable = $(tableEl).DataTable();
            return punchTable;
        }

        punchTable = PortalDataTable.init(tableEl, {
            order: [[1, "desc"]],
            pageLength: 25,
            dom: "<\"portal-dt-scroll\"t><\"dt-footer\"lip>",
            language: {
                info: "",
                infoEmpty: "",
                infoFiltered: "",
                zeroRecords: "No matching records. Punches will appear here the moment they arrive.",
            },
            drawCallback: function () {
                const info = this.api().table().container().querySelector(".dataTables_info");
                if (!info) return;
                const filtered = this.api().rows({ filter: "applied" }).count();
                const displayed = this.api().rows({ page: "current", filter: "applied" }).count();
                const synced = statSynced?.textContent?.trim() || "0";
                info.textContent = `Showing ${displayed} of ${filtered} records (filtered from ${synced} total)`;
            },
            columnDefs: [
                { orderable: false, searchable: false, targets: 0, width: "28px" },
                { orderable: false, searchable: false, targets: 9, width: "40px" },
            ],
            ajax: {
                url: "/Dashboard/RecentJson",
                dataSrc: function (json) {
                    if (statSynced && json.total != null) statSynced.textContent = String(json.total);
                    return json.rows || [];
                },
            },
            columns: [
                {
                    data: null,
                    defaultContent: "",
                    render: function () {
                        return '<span class="row-live-dot"></span>';
                    },
                    className: "col-dot",
                },
                {
                    data: "punchTime",
                    defaultContent: "",
                    render: function (d, type) {
                        if (window.PortalDateTime) return PortalDateTime.dataTablesRender(d, type);
                        return d || "";
                    },
                },
                {
                    data: "employeeId",
                    render: function (d) {
                        return d ? `<code class="emp-id">${escapeHtml(d)}</code>` : "—";
                    },
                },
                { data: "employeeName", defaultContent: "" },
                { data: "deviceName", defaultContent: "—" },
                { data: "deviceLocation", defaultContent: "—" },
                {
                    data: "deviceId",
                    render: function (d) {
                        return d != null ? escapeHtml(String(d)) : "—";
                    },
                },
                {
                    data: "status",
                    defaultContent: "",
                    render: function (d) {
                        if (!d) return "—";
                        return `<span class="badge">${escapeHtml(d)}</span>`;
                    },
                },
                {
                    data: "cardSource",
                    render: function (d) {
                        if (!d) return "—";
                        return `<span class="source-pill">${escapeHtml(d)}</span>`;
                    },
                },
                {
                    data: null,
                    defaultContent: "",
                    render: function () {
                        return '<button type="button" class="row-menu-btn" aria-label="Row options"><i class="bi bi-three-dots-vertical"></i></button>';
                    },
                    className: "col-actions",
                },
            ],
        });

        $.fn.dataTable.ext.search.push(function (settings, data) {
            if (settings.nTable !== tableEl) return true;
            if (!todayOnly) return true;
            const punch = (data[1] || "").slice(0, 10);
            return punch === todayIso();
        });

        searchBox?.addEventListener("input", () => {
            punchTable?.search(searchBox.value).draw();
        });

        filterToday?.addEventListener("click", () => {
            todayOnly = !todayOnly;
            filterToday.setAttribute("aria-pressed", todayOnly ? "true" : "false");
            filterToday.classList.toggle("is-off", !todayOnly);
            punchTable?.draw();
        });

        return punchTable;
    }

    function reloadGrid(keepPage) {
        if (punchTable) punchTable.ajax.reload(null, keepPage === false);
    }

    function applyDeviceStatus(d) {
        livePill?.classList.toggle("online", !!d.online);
        livePill?.classList.toggle("on", !!d.online);
        livePill?.classList.toggle("offline", !d.online);
        if (liveLabel) {
            if (pushMode && d.online) liveLabel.textContent = "Live · push (no 9922 poll)";
            else if (d.online) liveLabel.textContent = "Live · on-demand sync";
            else if (d.busy) liveLabel.textContent = "Device busy (9922)";
            else liveLabel.textContent = pushMode ? "Waiting for push…" : "Device offline";
        }
        if (d.online) {
            setText("statOnline", "Online");
            setText("statSerial", d.serialNumber || "—");
        } else {
            setText("statOnline", d.busy ? "Busy" : pushMode ? "Push idle" : "Offline");
            setText("statSerial", d.lastError ? String(d.lastError).slice(0, 56) : "—");
        }
        setText("statEmployees", d.employeeCount ?? "0");
        setText("statFaces", d.faceEnrolled ?? "0");
        setText("statOnDevice", d.recordCountOnDevice ?? "0");
        if (statSynced && d.total != null) statSynced.textContent = String(d.total);
    }

    function setText(id, val) {
        const el = document.getElementById(id);
        if (el) el.textContent = val;
    }

    async function refreshDeviceStatus() {
        try {
            const res = await fetch("/Dashboard/DeviceStatus");
            applyDeviceStatus(await res.json());
        } catch {
            livePill?.classList.add("offline");
            if (liveLabel) liveLabel.textContent = "Connection error";
        }
    }

    async function syncFromDevice() {
        if (pushMode || document.visibilityState !== "visible" || syncInFlight) return;
        syncInFlight = true;
        syncTick++;
        const wantInfo = syncTick === 1 || syncTick % 12 === 0;
        try {
            const res = await fetch(`/Dashboard/SyncNow?deviceInfo=${wantInfo ? "true" : "false"}`);
            const data = await res.json();
            if (!data.ok) return;
            applyDeviceStatus(data);
            if (data.newRows?.length || data.addedCount > 0) reloadGrid(false);
        } catch {
            livePill?.classList.add("offline");
            if (liveLabel) liveLabel.textContent = "Sync error";
        } finally {
            syncInFlight = false;
        }
    }

    function startSignalR() {
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/attendance", { withCredentials: true })
            .withAutomaticReconnect()
            .build();

        connection.on("NewPunches", () => {
            reloadGrid(true);
            refreshDeviceStatus();
        });

        connection
            .start()
            .then(() => {
                livePill?.classList.add("online", "on");
                if (liveLabel) liveLabel.textContent = pushMode ? "Live · push (no 9922 poll)" : "Live · connecting…";
                if (!pushMode) syncFromDevice();
                else refreshDeviceStatus();
            })
            .catch(() => {
                livePill?.classList.add("offline");
                if (liveLabel) liveLabel.textContent = "SignalR offline";
                if (!pushMode) syncFromDevice();
            });
    }

    $(function () {
        initDataTable();
        startSignalR();

        if (pushMode) {
            setInterval(() => {
                if (document.visibilityState === "visible") refreshDeviceStatus();
            }, 5000);
            setInterval(() => {
                if (document.visibilityState === "visible") reloadGrid(true);
            }, 4000);
        } else {
            setInterval(syncFromDevice, pollMs);
            document.addEventListener("visibilitychange", () => {
                if (document.visibilityState === "visible") syncFromDevice();
            });
        }
    });
})();
