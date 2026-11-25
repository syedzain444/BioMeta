(function () {
    "use strict";

    var idQueue = [];
    var table = null;
    var pulling = false;
    var rfidFilter = "assigned";
    var rfidSearchInstalled = false;
    var excelNicFilterInstalled = false;
    var excelNicFilterActive = false;
    var excelNicFilterSet = null;
    var pendingExcelWorkbook = null;
    var pendingExcelFileName = "";
    var fetchSource = "recent3d";

    var FETCH_LABEL = {
        queue: "Fetch queued IDs",
        logs: "Fetch from logs",
        db: "Fetch from portal DB",
        full: "Fetch full list",
        recent3d: "Fetch last 3 days enrolled",
    };

    var SOURCE_INFO = {
        logs: ["IDs come from the device logs", "Unique IDs found in GetRecord are used as the queue — nothing to paste."],
        db: ["IDs come from the portal database", "Distinct attendance IDs already in the portal are used as the queue — nothing to paste."],
        full: ["No ID list needed", "Pulls the complete user list from the device, like the device’s own User management screen."],
        recent3d: [
            "Rolling 3-day enroll window",
            "Scans every active device. Keeps users whose device register time (or first portal punch) falls on today and the previous two calendar days.",
        ],
    };

    var NIC_HEADER_RE =
        /^(nic|cnic|employee\s*id|emp\s*id|emp\s*no|id\s*no|national\s*id|employee\s*no|staff\s*id)$/i;

    var COL_SNO = 0;
    var COL_NIC = 1;
    var COL_NAME = 2;
    var COL_DEPT = 3;
    var COL_AUTH = 4;
    var COL_CARD = 5;
    var COL_FACES = 6;
    var COL_DEVICE = 7;
    var COL_ENROLLED = 8;
    var COL_IP = 9;
    var COL_PHOTO = 10;

    var EXPORT_COLUMNS = [
        { id: "sno", label: "S.No", always: true },
        { id: "nic", label: "Employee ID (NIC)", col: COL_NIC },
        { id: "name", label: "Name", col: COL_NAME },
        { id: "dept", label: "Department", col: COL_DEPT },
        { id: "auth", label: "Authority", col: COL_AUTH },
        { id: "card", label: "Card / RFID", col: COL_CARD },
        { id: "faces", label: "Faces", col: COL_FACES },
        { id: "device", label: "Device", col: COL_DEVICE },
        { id: "enrolled", label: "Enrolled", col: COL_ENROLLED },
        { id: "ip", label: "Device IP", col: COL_IP },
    ];

    function escAttr(s) {
        return String(s || "")
            .replace(/&/g, "&amp;")
            .replace(/"/g, "&quot;")
            .replace(/</g, "&lt;");
    }

    function photoCellHtml(e) {
        if (!e) return "";
        var id = e.employeeId || "";
        var name = e.name || "";
        var devId = e.bioDeviceId != null ? e.bioDeviceId : "";
        var face = e.faceTemplateCount != null ? e.faceTemplateCount : 0;
        var has = !!e.hasPhoto && e.photoUrl;
        var url = e.photoUrl || "";
        var ring = has ? "geu-photo-ring--ok" : face > 0 ? "geu-photo-ring--tpl" : "geu-photo-ring--none";
        var gallery =
            e.photosPageUrl || "/Device/EmployeePhotos?q=" + encodeURIComponent(id);
        var dlQs = "?download=1&name=" + encodeURIComponent(name);
        var inner = has
            ? '<img class="geu-photo-thumb-img" src="' +
              url +
              (url.indexOf("?") >= 0 ? "&" : "?") +
              "t=" +
              Date.now() +
              '" alt="" loading="lazy" width="44" height="44" />'
            : '<span class="geu-photo-thumb-empty" aria-hidden="true">?</span>';
        var actions = has
            ? '<a class="geu-photo-act" href="' +
              url +
              '" target="_blank" rel="noopener">View</a><a class="geu-photo-act" href="' +
              url +
              dlQs +
              '">Download</a>'
            : '<a class="geu-photo-act geu-photo-act-muted" href="' + gallery + '">Open</a>';
        return (
            '<div class="geu-photo-cell" data-device-id="' +
            escAttr(devId) +
            '" data-employee-id="' +
            escAttr(id) +
            '" data-name="' +
            escAttr(name) +
            '" data-has-photo="' +
            (has ? "1" : "0") +
            '">' +
            '<div class="geu-photo-ring ' +
            ring +
            '">' +
            inner +
            "</div>" +
            '<div class="geu-photo-actions">' +
            actions +
            "</div></div>"
        );
    }

    function collectBulkPhotoItems() {
        if (!table) return [];
        var items = [];
        table.rows().every(function () {
            var row = this.data();
            if (!row || !row[COL_PHOTO]) return;
            var wrap = document.createElement("div");
            wrap.innerHTML = row[COL_PHOTO];
            var cell = wrap.querySelector(".geu-photo-cell");
            if (!cell || cell.getAttribute("data-has-photo") !== "1") return;
            var devId = parseInt(cell.getAttribute("data-device-id"), 10);
            var empId = cell.getAttribute("data-employee-id");
            if (!devId || !empId) return;
            items.push({
                deviceId: devId,
                employeeId: empId,
                name: cell.getAttribute("data-name") || "",
            });
        });
        return items;
    }

    function setBulkPhotosVisible(show) {
        var btn = document.getElementById("btnBulkDownloadPhotos");
        if (!btn) return;
        var hasPhotos = collectBulkPhotoItems().length > 0;
        if (show && hasPhotos) btn.hidden = false;
        else if (!show) btn.hidden = true;
    }

    async function bulkDownloadPhotos() {
        var items = collectBulkPhotoItems();
        if (!items.length) {
            setStat("No photos in grid — fetch users first (大头照 on device).", "warn");
            return;
        }
        var btn = document.getElementById("btnBulkDownloadPhotos");
        if (btn) btn.disabled = true;
        setStat("Building ZIP (" + items.length + " photos)…", "run");
        try {
            var res = await fetch("/Device/EnrolledUsers/Photos/BulkDownload", {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ items: items }),
            });
            if (!res.ok) {
                var errJson = null;
                try {
                    errJson = await res.json();
                } catch (e2) {}
                setStat((errJson && errJson.error) || "Bulk download failed.", "warn");
                return;
            }
            var blob = await res.blob();
            var cd = res.headers.get("Content-Disposition") || "";
            var fileName = "employee-photos.zip";
            var m = /filename="?([^";]+)"?/i.exec(cd);
            if (m) fileName = m[1];
            var a = document.createElement("a");
            a.href = URL.createObjectURL(blob);
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            a.remove();
            setStat("Downloaded " + items.length + " photo(s) as ZIP (Name_ID inside).", "ok");
        } catch (e) {
            setStat(e.message || "Bulk download failed.", "warn");
        } finally {
            if (btn) btn.disabled = false;
        }
    }

    function isGateosEnrolledPage() {
        return document.body.classList.contains("gateos-page-enrolled");
    }

    function deviceId() {
        var sel = document.getElementById("enrolledDeviceSelect");
        if (sel && sel.value) return parseInt(sel.value, 10);
        var v = document.getElementById("filterDeviceId");
        return v && v.value ? parseInt(v.value, 10) : null;
    }

    function setDeviceIdHidden(id) {
        var v = document.getElementById("filterDeviceId");
        if (v) v.value = id != null ? String(id) : "";
    }

    function normalizeDeviceJsonRow(d) {
        if (!d) return null;
        var id = d.deviceId != null ? d.deviceId : d.DeviceId;
        if (id == null) return null;
        return {
            deviceId: id,
            deviceName: (d.deviceName != null ? d.deviceName : d.DeviceName) || "Device",
            deviceIp: (d.deviceIp != null ? d.deviceIp : d.DeviceIp) || "",
        };
    }

    function parseDevicesJson() {
        var el = document.getElementById("devicesJson");
        if (!el || !el.textContent) return [];
        try {
            var raw = JSON.parse(el.textContent);
            if (!Array.isArray(raw)) return [];
            return raw.map(normalizeDeviceJsonRow).filter(Boolean);
        } catch (e) {
            return [];
        }
    }

    function initDeviceSelect() {
        var sel = document.getElementById("enrolledDeviceSelect");
        if (!sel) return;
        var list = parseDevicesJson();
        list.forEach(function (d) {
            var opt = document.createElement("option");
            opt.value = String(d.deviceId);
            opt.textContent = d.deviceName + " (" + d.deviceIp + ")";
            sel.appendChild(opt);
        });
        var initial = deviceId();
        if (initial && sel.querySelector('option[value="' + initial + '"]')) {
            sel.value = String(initial);
        } else if (list.length === 1) {
            sel.value = String(list[0].deviceId);
            setDeviceIdHidden(list[0].deviceId);
        }
        sel.addEventListener("change", function () {
            var id = sel.value ? parseInt(sel.value, 10) : null;
            setDeviceIdHidden(id);
            refreshGateosDeviceLabels();
        });
        if (!list.length) {
            setStat("No active devices in portal — check Admin → Devices.", "warn");
        }
        var allChk = document.getElementById("chkAllDevices");
        function syncAllDevicesUi() {
            var on = allChk && allChk.checked;
            sel.disabled = !!on;
            sel.classList.toggle("is-disabled", !!on);
        }
        if (allChk) {
            allChk.addEventListener("change", function () {
                syncAllDevicesUi();
                refreshGateosDeviceLabels();
                refreshGateosSourceUi();
            });
            syncAllDevicesUi();
        }
        refreshGateosDeviceLabels();
    }

    function badgeText(el, text) {
        if (!el) return;
        var span = el.querySelector(".enrolled-badge-text");
        if (span) span.textContent = text;
        else el.textContent = text;
    }

    function setStat(text, kind) {
        var el = document.getElementById("idQueueStat");
        if (el) {
            badgeText(el, text);
            el.classList.remove("enrolled-badge--idle", "enrolled-badge--ready", "enrolled-badge--warn");
            if (kind === "ready") el.classList.add("enrolled-badge--ready");
            else if (kind === "warn") el.classList.add("enrolled-badge--warn");
            else el.classList.add("enrolled-badge--idle");
        }
        if (isGateosEnrolledPage()) {
            var sbText = document.getElementById("sbText");
            if (sbText && text) sbText.textContent = text;
            else if (sbText && !text && !pulling) sbText.textContent = "Ready";
            var qCount = document.getElementById("qCount");
            if (qCount) {
                qCount.textContent =
                    fetchSource === "queue" ? String(idQueue.length) : "—";
            }
        }
    }

    function setProgress(text, pct) {
        var el = document.getElementById("pullProgress");
        var wrap = document.getElementById("pullProgressWrap");
        var fill = document.getElementById("pullProgressFill");
        var geuBar = document.getElementById("geuBar");
        var sbState = document.getElementById("sbState");
        var pillA = document.getElementById("pillA");
        var pillB = document.getElementById("pillB");
        if (!text) {
            if (el) {
                el.classList.add("is-empty");
                badgeText(el, "");
            }
            if (wrap) wrap.hidden = true;
            if (fill) fill.style.width = "0%";
            if (geuBar) {
                geuBar.classList.remove("ind");
                geuBar.style.width = "0%";
            }
            if (sbState) sbState.classList.remove("run");
            return;
        }
        if (el) {
            el.classList.remove("is-empty", "enrolled-badge--progress", "enrolled-badge--done", "enrolled-badge--warn");
            badgeText(el, text);
            if (typeof pct === "number" && pct >= 100) el.classList.add("enrolled-badge--done");
            else if (text.indexOf("Stopped") === 0) el.classList.add("enrolled-badge--warn");
            else el.classList.add("enrolled-badge--progress");
        }
        if (wrap && typeof pct === "number") {
            wrap.hidden = false;
            if (fill) fill.style.width = Math.min(100, Math.max(0, pct)) + "%";
        }
        if (isGateosEnrolledPage()) {
            if (sbState) sbState.classList.add("run");
            var sbText = document.getElementById("sbText");
            if (sbText) sbText.textContent = text;
            if (pillA && text) pillA.textContent = text;
            if (pillB && text.indexOf("full") >= 0) pillB.textContent = text;
            if (geuBar && typeof pct === "number") {
                geuBar.classList.remove("ind");
                geuBar.style.width = Math.min(100, Math.max(0, pct)) + "%";
            } else if (geuBar && pct == null) {
                geuBar.classList.add("ind");
            }
        }
    }

    function setExportExcelVisible(show) {
        var btn = document.getElementById("btnExportExcel");
        if (!btn) return;
        var hasRows = table && table.rows({ search: "none" }).count() > 0;
        if (show && hasRows) {
            btn.hidden = false;
        } else if (!show) {
            btn.hidden = true;
        }
    }

    function swalOperationComplete(html) {
        if (typeof Swal === "undefined") {
            var plain = html ? html.replace(/<[^>]*>/g, " ").replace(/\s+/g, " ").trim() : "";
            window.alert("Operation complete.\n" + (plain || "The fetch finished successfully."));
            return;
        }
        Swal.fire({
            icon: "success",
            title: "Operation complete",
            html: html || "The operation finished successfully.",
            confirmButtonText: "OK",
            confirmButtonColor: "#059669",
        });
    }

    function updateCountPill() {
        if (!isGateosEnrolledPage() || !table) return;
        var n = table.rows({ search: "applied" }).count();
        var pill = document.getElementById("countPill");
        if (pill) pill.textContent = n + (n === 1 ? " user" : " users");
    }

    function faceBadgeRender(data) {
        var raw = String(data || "");
        if (raw.indexOf("geu-bio-badge") >= 0) return raw;
        var n = parseInt(raw, 10);
        var enrolled = !isNaN(n) && n > 0;
        var cls = enrolled ? "geu-bio-badge ok" : "geu-bio-badge pending";
        return (
            '<span class="' +
            cls +
            '">' +
            (enrolled ? "Face enrolled" : "No template") +
            (enrolled ? ' <small>×' + n + "</small>" : "") +
            "</span>"
        );
    }

    function biometricCellHtml(e) {
        var n = e.faceTemplateCount != null ? Number(e.faceTemplateCount) : 0;
        var label = e.biometricLabel || (n > 0 ? "Face enrolled" : "No template");
        var st = String(e.biometricStatus || "").toLowerCase();
        var cls = "geu-bio-badge";
        if (st.indexOf("face") >= 0) cls += " ok";
        else if (st.indexOf("finger") >= 0) cls += " finger";
        else if (st.indexOf("card") >= 0) cls += " card";
        else cls += " pending";
        return (
            '<span class="' +
            cls +
            '">' +
            label +
            (n > 0 ? ' <small>×' + n + "</small>" : "") +
            "</span>"
        );
    }

    function refreshGateosDeviceLabels() {
        if (!isGateosEnrolledPage()) return;
        var sel = document.getElementById("enrolledDeviceSelect");
        var allChk = document.getElementById("chkAllDevices");
        var label = "No device";
        var shortName = "—";
        var ip = "";
        if (allChk && allChk.checked) {
            label = "All active devices";
            shortName = "All devices";
        } else if (sel && sel.selectedIndex > 0) {
            label = sel.options[sel.selectedIndex].textContent || label;
            var m = label.match(/^(.+?)\s*\(([^)]+)\)/);
            if (m) {
                shortName = m[1].trim();
                ip = m[2].trim();
            } else shortName = label;
        }
        var titleDev = document.getElementById("enrolledTitleDev");
        if (titleDev) titleDev.textContent = shortName;
        var hero = document.getElementById("geuHeroDevice");
        if (hero) hero.textContent = label;
        var sbDev = document.getElementById("sbDev");
        if (sbDev) sbDev.textContent = ip ? shortName + " · " + ip : shortName;
    }

    function refreshGateosSourceUi() {
        if (!isGateosEnrolledPage()) return;
        var idsPane = document.getElementById("idsPane");
        var noIds = document.getElementById("noIds");
        if (idsPane) idsPane.hidden = fetchSource !== "queue";
        if (noIds) noIds.hidden = fetchSource === "queue";
        if (fetchSource !== "queue" && SOURCE_INFO[fetchSource]) {
            var t = document.getElementById("noIdsTitle");
            var x = document.getElementById("noIdsText");
            if (t) t.textContent = SOURCE_INFO[fetchSource][0];
            if (x) x.textContent = " " + SOURCE_INFO[fetchSource][1];
        }
        var fetchLbl = document.getElementById("fetchLbl");
        if (fetchLbl) fetchLbl.textContent = FETCH_LABEL[fetchSource] || "Fetch";
        var fetchBtn = document.getElementById("fetchBtn");
        if (fetchBtn) {
            fetchBtn.disabled =
                pulling || (fetchSource === "queue" && !idQueue.length && !String((document.getElementById("nicList") || {}).value || "").trim());
        }
        var bCount = document.getElementById("bCount");
        var batch = document.getElementById("batchSize");
        if (bCount && batch) bCount.textContent = batch.value || "35";
        refreshGateosDeviceLabels();
    }

    function initGateosSourceUi() {
        if (!isGateosEnrolledPage()) return;
        document.querySelectorAll(".geu-opt").forEach(function (btn) {
            btn.addEventListener("click", function () {
                fetchSource = btn.getAttribute("data-src") || "full";
                document.querySelectorAll(".geu-opt").forEach(function (o) {
                    o.setAttribute("aria-checked", o === btn ? "true" : "false");
                });
                if (fetchSource === "recent3d") {
                    var chk = document.getElementById("chkAllDevices");
                    if (chk) chk.checked = true;
                }
                refreshGateosSourceUi();
            });
        });
        var helpBtn = document.getElementById("enrolledHelpBtn");
        var pop = document.getElementById("enrolledHelpPop");
        function closePop() {
            if (pop) pop.hidden = true;
            if (helpBtn) helpBtn.setAttribute("aria-expanded", "false");
        }
        if (helpBtn && pop) {
            helpBtn.addEventListener("click", function (e) {
                e.stopPropagation();
                pop.hidden = !pop.hidden;
                helpBtn.setAttribute("aria-expanded", pop.hidden ? "false" : "true");
            });
            document.addEventListener("click", function (e) {
                if (!pop.contains(e.target) && e.target !== helpBtn) closePop();
            });
            document.addEventListener("keydown", function (e) {
                if (e.key === "Escape") closePop();
            });
        }
        var attachBtn = document.getElementById("attachBtn");
        var fileInput = document.getElementById("nicExcelFile");
        if (attachBtn && fileInput) {
            attachBtn.addEventListener("click", function () {
                fileInput.click();
            });
        }
        var search = document.getElementById("enrolledGridSearch");
        if (search) {
            search.addEventListener("input", function () {
                if (table) {
                    table.search(search.value).draw();
                    updateCountPill();
                }
            });
        }
        var batch = document.getElementById("batchSize");
        if (batch) batch.addEventListener("input", refreshGateosSourceUi);
        var fetchBtn = document.getElementById("fetchBtn");
        if (fetchBtn) {
            fetchBtn.addEventListener("click", function () {
                runGateosFetch();
            });
        }
        refreshGateosSourceUi();
    }

    async function runGateosFetch() {
        if (pulling) return;
        var runPills = document.getElementById("runPills");
        if (fetchSource === "full") {
            if (runPills) runPills.hidden = false;
            await pullAllFromDeviceList();
            if (runPills) runPills.hidden = true;
            refreshGateosSourceUi();
            return;
        }
        if (fetchSource === "recent3d") {
            if (runPills) runPills.hidden = false;
            await pullRecentEnrolledFromAllDevices();
            if (runPills) runPills.hidden = true;
            refreshGateosSourceUi();
            return;
        }
        if (fetchSource === "logs") await loadIds("device");
        else if (fetchSource === "db") await loadIds("portal");
        else if (fetchSource === "queue") {
            var ok = await ensureIdQueueFromInputs();
            if (!ok) {
                setStat("Paste NICs or attach Excel first.", "warn");
                refreshGateosSourceUi();
                return;
            }
        }
        if (fetchSource !== "full" && !idQueue.length) {
            refreshGateosSourceUi();
            return;
        }
        await pullAll();
        refreshGateosSourceUi();
    }

    function normalizeClient(raw) {
        return String(raw || "").replace(/\D/g, "");
    }

    function hasRfid(cardCell) {
        var card = String(cardCell || "").trim();
        return card.length > 0 && card !== "—" && card !== "-";
    }

    function installRfidFilter() {
        if (rfidSearchInstalled || !window.jQuery) return;
        rfidSearchInstalled = true;
        window.jQuery.fn.dataTable.ext.search.push(function (settings, data) {
            if (!settings.nTable || settings.nTable.id !== "enrolledTable") return true;
            var card = data[COL_CARD];
            var assigned = hasRfid(card);
            if (rfidFilter === "assigned") return assigned;
            if (rfidFilter === "unassigned") return !assigned;
            return true;
        });
    }

    function installExcelNicFilter() {
        if (excelNicFilterInstalled || !window.jQuery) return;
        excelNicFilterInstalled = true;
        window.jQuery.fn.dataTable.ext.search.push(function (settings, data) {
            if (!settings.nTable || settings.nTable.id !== "enrolledTable") return true;
            if (!excelNicFilterActive || !excelNicFilterSet || !excelNicFilterSet.size) return true;
            var nic = normalizeClient(data[COL_NIC]);
            return excelNicFilterSet.has(nic);
        });
    }

    function setExcelImportStatus(text, kind) {
        var el = document.getElementById("excelImportStatus");
        var clearBtn = document.getElementById("btnClearExcelNic");
        if (!el) return;
        if (!text) {
            el.textContent = "";
            el.classList.add("is-hidden", "geu-hidden");
            el.classList.remove("enrolled-excel-status--warn", "enrolled-excel-status--ok");
            if (clearBtn && !excelNicFilterActive) clearBtn.classList.add("is-hidden", "geu-hidden");
            return;
        }
        el.textContent = text;
        el.classList.remove("is-hidden", "geu-hidden", "enrolled-excel-status--warn", "enrolled-excel-status--ok");
        if (kind === "warn") el.classList.add("enrolled-excel-status--warn");
        else if (kind === "ok") el.classList.add("enrolled-excel-status--ok");
        if (clearBtn) clearBtn.classList.toggle("is-hidden", !excelNicFilterActive);
    }

    function applyExcelGridFilter(ids) {
        var chk = document.getElementById("excelFilterGrid");
        if (!chk || !chk.checked || !ids || !ids.length) {
            excelNicFilterActive = false;
            excelNicFilterSet = null;
            setExcelImportStatus("", null);
            if (table) table.draw(false);
            return;
        }
        excelNicFilterSet = new Set(
            ids.map(function (id) {
                return normalizeClient(id);
            })
        );
        excelNicFilterActive = true;
        setExcelImportStatus(
            excelNicFilterSet.size + " NIC(s) from Excel — grid filtered to match",
            "ok"
        );
        if (table) {
            table.draw(false);
            syncIdCardToFilteredRows();
        }
    }

    function clearExcelNicImport() {
        excelNicFilterActive = false;
        excelNicFilterSet = null;
        pendingExcelWorkbook = null;
        pendingExcelFileName = "";
        var file = document.getElementById("nicExcelFile");
        if (file) file.value = "";
        var colSel = document.getElementById("excelNicColumn");
        if (colSel) {
            colSel.innerHTML = '<option value="auto">Column: Auto-detect</option>';
            colSel.disabled = true;
        }
        var importBtn = document.getElementById("btnImportNicExcel");
        if (importBtn) importBtn.disabled = true;
        setExcelImportStatus("", null);
        var clearBtn = document.getElementById("btnClearExcelNic");
        if (clearBtn) clearBtn.classList.add("is-hidden");
        if (table) {
            table.draw(false);
            syncIdCardToFilteredRows();
        }
    }

    function columnLabel(index, headerText) {
        var letter = "";
        var n = index;
        do {
            letter = String.fromCharCode(65 + (n % 26)) + letter;
            n = Math.floor(n / 26) - 1;
        } while (n >= 0);
        var h = headerText != null ? String(headerText).trim() : "";
        if (h) return "Column " + letter + " — " + h;
        return "Column " + letter;
    }

    function sheetRowsFromWorkbook(wb) {
        if (!wb || !wb.SheetNames || !wb.SheetNames.length) return [];
        var sheet = wb.Sheets[wb.SheetNames[0]];
        if (!sheet || !window.XLSX) return [];
        return window.XLSX.utils.sheet_to_json(sheet, { header: 1, defval: "", raw: false });
    }

    function looksLikeNicValue(v) {
        var digits = normalizeClient(v);
        return digits.length >= 10 && digits.length <= 15;
    }

    function detectNicColumnIndex(rows) {
        if (!rows || !rows.length) return 0;
        var header = rows[0].map(function (c) {
            return String(c || "").trim();
        });
        var c;
        for (c = 0; c < header.length; c++) {
            if (NIC_HEADER_RE.test(header[c])) return c;
        }
        var bestCol = 0;
        var bestScore = -1;
        var startRow = 0;
        var headerHits = 0;
        for (c = 0; c < header.length; c++) {
            if (NIC_HEADER_RE.test(header[c]) || /id|nic|cnic/i.test(header[c])) headerHits++;
        }
        if (headerHits > 0) startRow = 1;
        for (c = 0; c < header.length; c++) {
            var score = 0;
            var r;
            for (r = startRow; r < Math.min(rows.length, startRow + 200); r++) {
                if (rows[r] && looksLikeNicValue(rows[r][c])) score++;
            }
            if (score > bestScore) {
                bestScore = score;
                bestCol = c;
            }
        }
        return bestCol;
    }

    function populateExcelColumnSelect(rows) {
        var sel = document.getElementById("excelNicColumn");
        if (!sel || !rows.length) return;
        var header = rows[0] || [];
        var colCount = rows.reduce(function (max, row) {
            return Math.max(max, row ? row.length : 0);
        }, header.length);
        sel.innerHTML = '<option value="auto">Column: Auto-detect</option>';
        var c;
        for (c = 0; c < colCount; c++) {
            var opt = document.createElement("option");
            opt.value = String(c);
            opt.textContent = columnLabel(c, header[c]);
            sel.appendChild(opt);
        }
        sel.disabled = false;
        sel.value = String(detectNicColumnIndex(rows));
    }

    var NAME_HEADER_RE = /^(name|employee\s*name|full\s*name)$/i;

    function detectNameColumnIndex(rows, nicCol) {
        if (!rows || !rows.length) return -1;
        var header = rows[0] || [];
        var c;
        for (c = 0; c < header.length; c++) {
            if (c === nicCol) continue;
            if (NAME_HEADER_RE.test(String(header[c] || "").trim())) return c;
        }
        return header.length > 1 && nicCol !== 1 ? 1 : -1;
    }

    function extractImportRowsFromWorkbook() {
        if (!pendingExcelWorkbook) return [];
        var rows = sheetRowsFromWorkbook(pendingExcelWorkbook);
        if (!rows.length) return [];
        var colSel = document.getElementById("excelNicColumn");
        var choice = colSel && !colSel.disabled ? colSel.value : "auto";
        var nicCol =
            choice === "auto" || choice == null || choice === ""
                ? detectNicColumnIndex(rows)
                : parseInt(choice, 10);
        if (isNaN(nicCol) || nicCol < 0) nicCol = 0;
        var nameCol = detectNameColumnIndex(rows, nicCol);
        var startRow = 0;
        var header = rows[0] || [];
        if (header[nicCol] != null && NIC_HEADER_RE.test(String(header[nicCol]).trim())) startRow = 1;
        var out = [];
        var r;
        for (r = startRow; r < rows.length; r++) {
            var row = rows[r];
            if (!row) continue;
            var nic = normalizeClient(row[nicCol]);
            if (!nic || nic.length < 1) continue;
            out.push({
                line: r + 1,
                employeeId: nic,
                name: nameCol >= 0 ? String(row[nameCol] || "").trim() : "",
            });
        }
        return out;
    }

    function extractNicsFromRows(rows, columnChoice) {
        if (!rows || !rows.length) return [];
        var col =
            columnChoice === "auto" || columnChoice == null || columnChoice === ""
                ? detectNicColumnIndex(rows)
                : parseInt(columnChoice, 10);
        if (isNaN(col) || col < 0) col = 0;
        var startRow = 0;
        var header = rows[0] || [];
        if (header[col] != null && NIC_HEADER_RE.test(String(header[col]).trim())) startRow = 1;
        else if (/nic|cnic|employee\s*id|emp\s*id/i.test(String(header[col] || ""))) startRow = 1;

        var seen = {};
        var out = [];
        var r;
        for (r = startRow; r < rows.length; r++) {
            var cell = rows[r] ? rows[r][col] : "";
            if (cell == null || String(cell).trim() === "") continue;
            var nic = normalizeClient(cell);
            if (nic.length < 10) continue;
            if (seen[nic]) continue;
            seen[nic] = true;
            out.push(nic);
        }
        return out;
    }

    function readExcelFile(file) {
        return new Promise(function (resolve, reject) {
            var reader = new FileReader();
            reader.onload = function (ev) {
                try {
                    if (!window.XLSX) {
                        reject(new Error("Excel library not loaded"));
                        return;
                    }
                    var data = new Uint8Array(ev.target.result);
                    var wb = window.XLSX.read(data, { type: "array" });
                    resolve(wb);
                } catch (err) {
                    reject(err);
                }
            };
            reader.onerror = function () {
                reject(new Error("Could not read file"));
            };
            reader.readAsArrayBuffer(file);
        });
    }

    async function onNicExcelFileSelected(file) {
        if (!file) {
            pendingExcelWorkbook = null;
            pendingExcelFileName = "";
            return;
        }
        if (!window.XLSX) {
            setStat("Excel library not loaded. Refresh the page.", "warn");
            return;
        }
        try {
            pendingExcelWorkbook = await readExcelFile(file);
            pendingExcelFileName = file.name || "workbook";
            var rows = sheetRowsFromWorkbook(pendingExcelWorkbook);
            if (!rows.length) {
                setExcelImportStatus("No rows found in the first sheet.", "warn");
                return;
            }
            populateExcelColumnSelect(rows);
            var importBtn = document.getElementById("btnImportNicExcel");
            if (importBtn) importBtn.disabled = false;
            await importNicExcel();
        } catch (e) {
            pendingExcelWorkbook = null;
            setExcelImportStatus((e && e.message) || "Invalid Excel file.", "warn");
        }
    }

    async function importNicExcel() {
        if (!pendingExcelWorkbook) {
            setExcelImportStatus("Attach an Excel file first.", "warn");
            return false;
        }
        var rows = sheetRowsFromWorkbook(pendingExcelWorkbook);
        var colSel = document.getElementById("excelNicColumn");
        var choice = colSel && !colSel.disabled ? colSel.value : "auto";
        var nics = extractNicsFromRows(rows, choice);
        if (!nics.length) {
            setExcelImportStatus("No NIC values found in the selected column.", "warn");
            setStat("No NIC values found in Excel column.", "warn");
            return false;
        }
        var ta = document.getElementById("nicList");
        if (ta) ta.value = nics.join("\n");
        idQueue = nics.slice();
        setStat(idQueue.length + " IDs queued from Excel", "ready");
        setExcelImportStatus(
            idQueue.length + ' NIC(s) imported from "' + pendingExcelFileName + '"',
            "ok"
        );
        applyExcelGridFilter(idQueue);
        refreshGateosSourceUi();
        return true;
    }

    async function ensureIdQueueFromInputs() {
        if (idQueue.length) return true;
        var ta = document.getElementById("nicList");
        var text = ta ? String(ta.value || "") : "";
        if (text.trim()) {
            await resolvePastedFromText(text);
            return idQueue.length > 0;
        }
        if (pendingExcelWorkbook) {
            return importNicExcel();
        }
        return false;
    }

    function downloadNicTemplate() {
        if (!window.XLSX) {
            setStat("Excel library not loaded. Refresh the page.", "warn");
            return;
        }
        var aoa = [
            ["Employee ID (NIC)", "Name (optional)"],
            ["4220112345671", "Example employee"],
            ["43503-8549589-5", "Dashes are OK"],
        ];
        var ws = window.XLSX.utils.aoa_to_sheet(aoa);
        var wb = window.XLSX.utils.book_new();
        window.XLSX.utils.book_append_sheet(wb, ws, "NIC list");
        window.XLSX.writeFile(wb, "nic-import-template.xlsx");
    }

    function bindNicExcelImport() {
        var fileInput = document.getElementById("nicExcelFile");
        if (fileInput) {
            fileInput.addEventListener("change", function () {
                var file = fileInput.files && fileInput.files[0];
                onNicExcelFileSelected(file || null);
            });
        }
        var importBtn = document.getElementById("btnImportNicExcel");
        if (importBtn) importBtn.addEventListener("click", function () {
            importNicExcel();
        });
        var colSel = document.getElementById("excelNicColumn");
        if (colSel) {
            colSel.addEventListener("change", function () {
                if (pendingExcelWorkbook) importNicExcel();
            });
        }
        var clearBtn = document.getElementById("btnClearExcelNic");
        if (clearBtn) clearBtn.addEventListener("click", clearExcelNicImport);
        var tplBtn = document.getElementById("btnDownloadNicTemplate");
        if (tplBtn) tplBtn.addEventListener("click", downloadNicTemplate);
        var filterChk = document.getElementById("excelFilterGrid");
        if (filterChk) {
            filterChk.addEventListener("change", function () {
                if (idQueue.length) applyExcelGridFilter(idQueue);
                else clearExcelNicImport();
            });
        }
    }

    function formatIssueDate() {
        var d = new Date();
        return d.toLocaleDateString("en-GB", {
            weekday: "short",
            day: "2-digit",
            month: "short",
            year: "numeric",
        });
    }

    function refreshIssueDate() {
        var el = document.getElementById("idCardIssueDate");
        if (el) el.textContent = formatIssueDate();
    }

    function showIdCard(rowData) {
        refreshIssueDate();
        if (!rowData) return;
        var nameEl = document.getElementById("idCardName");
        var nicEl = document.getElementById("idCardNic");
        var rfidEl = document.getElementById("idCardRfid");
        if (nameEl) nameEl.textContent = rowData[COL_NAME] || "—";
        if (nicEl) nicEl.textContent = rowData[COL_NIC] || "—";
        if (rfidEl) {
            rfidEl.textContent = hasRfid(rowData[COL_CARD]) ? rowData[COL_CARD] : "Not assigned";
        }
    }

    function syncIdCardToFilteredRows() {
        refreshIssueDate();
        if (!table) return;
        var indexes = table.rows({ search: "applied" }).indexes();
        if (indexes.length > 0) {
            showIdCard(table.row(indexes[0]).data());
            return;
        }
        var nameEl = document.getElementById("idCardName");
        var nicEl = document.getElementById("idCardNic");
        var rfidEl = document.getElementById("idCardRfid");
        if (nameEl) {
            nameEl.textContent =
                rfidFilter === "unassigned" ? "RFID unassigned — select a row" : "Select a row";
        }
        if (nicEl) nicEl.textContent = "—";
        if (rfidEl) rfidEl.textContent = rfidFilter === "unassigned" ? "Not assigned" : "—";
    }

    function setRfidTab(filter) {
        rfidFilter = filter;
        document.querySelectorAll(".enrolled-rfid-tab").forEach(function (btn) {
            var on = btn.getAttribute("data-rfid-filter") === filter;
            btn.classList.toggle("is-active", on);
            btn.setAttribute("aria-selected", on ? "true" : "false");
        });
        refreshIssueDate();
        if (table) {
            table.draw(false);
            syncIdCardToFilteredRows();
        }
    }

    function snoRender(_data, type, _row, meta) {
        var n = meta.settings._iDisplayStart + meta.row + 1;
        return type === "sort" || type === "type" ? n : String(n);
    }

    function initTable() {
        var tableEl = document.getElementById("enrolledTable");
        if (!tableEl || !window.PortalDataTable || !window.jQuery) return;
        installRfidFilter();
        installExcelNicFilter();
        if (window.jQuery.fn.DataTable.isDataTable(tableEl)) {
            table = window.jQuery(tableEl).DataTable();
            return;
        }
        var dtOpts = {
            order: [[COL_NIC, "asc"]],
            pageLength: 50,
            columnDefs: [
                {
                    targets: COL_SNO,
                    orderable: false,
                    searchable: false,
                    className: "dt-col-sno col-sno",
                    render: snoRender,
                },
                { targets: COL_DEPT, className: "col-dept" },
                { targets: COL_AUTH, className: "col-auth" },
                { targets: COL_CARD, className: "col-card" },
                { targets: COL_ENROLLED, className: "col-enrolled" },
                { targets: COL_IP, className: "col-ip" },
                {
                    targets: COL_PHOTO,
                    orderable: false,
                    searchable: false,
                    className: "col-photo",
                    render: function (d) {
                        return d || "";
                    },
                },
            ],
        };
        if (isGateosEnrolledPage()) {
            dtOpts.dom = "t";
            dtOpts.paging = false;
            dtOpts.searching = true;
            dtOpts.info = false;
            dtOpts.lengthChange = false;
            dtOpts.language = {
                zeroRecords:
                    "No users loaded yet — choose a source, then press Fetch to pull enrolled profiles from the device.",
            };
            dtOpts.columnDefs.push({
                targets: COL_FACES,
                render: function (d) {
                    return faceBadgeRender(d);
                },
            });
            dtOpts.columnDefs.push({
                targets: COL_ENROLLED,
                render: function (d, type) {
                    if (type === "sort" || type === "type") {
                        return window.PortalDateTime ? PortalDateTime.sortKey(d) : d || "";
                    }
                    if (!d) return '<span class="geu-muted">—</span>';
                    var html = window.PortalDateTime
                        ? PortalDateTime.formatHtml(d)
                        : d;
                    return '<span class="geu-enrolled">' + html + "</span>";
                },
            });
            dtOpts.columnDefs.push({
                targets: COL_IP,
                render: function (d) {
                    if (!d) return '<span class="geu-muted">—</span>';
                    return '<code class="geu-ip">' + d + "</code>";
                },
            });
        }
        table = PortalDataTable.init(tableEl, dtOpts);
        window.jQuery(tableEl).on("click", "tbody tr", function () {
            var row = table.row(this).data();
            if (row) showIdCard(row);
        });
        window.jQuery(tableEl).on("dblclick", "tbody tr", function (ev) {
            var row = table.row(this).data();
            if (row && row[COL_NIC]) {
                ev.preventDefault();
                var id = encodeURIComponent(row[COL_NIC]);
                window.location.href = "/Device/EmployeePhotos?q=" + id;
            }
        });
        table.on("draw", function () {
            refreshIssueDate();
            updateCountPill();
            setBulkPhotosVisible(table.rows({ search: "none" }).count() > 0);
        });
        syncIdCardToFilteredRows();
        updateCountPill();
    }

    function rowsFromEmployees(list) {
        return (list || []).map(function (e) {
            return [
                "",
                e.employeeId || "",
                e.name || "",
                e.department || "",
                e.authority || "",
                e.cardNumber || "",
                biometricCellHtml(e),
                e.deviceName || "",
                e.enrolledAt || "",
                e.deviceIp || "",
                photoCellHtml(e),
            ];
        });
    }

    function mergeIntoTable(newRows) {
        if (!table) return;
        var existing = table.rows().data().toArray();
        var seen = {};
        existing.forEach(function (r) {
            seen[r[COL_NIC] + "|" + r[COL_DEVICE]] = true;
        });
        newRows.forEach(function (r) {
            var key = r[COL_NIC] + "|" + r[COL_DEVICE];
            if (!seen[key]) {
                seen[key] = true;
                existing.push(r);
            }
        });
        table.clear();
        table.rows.add(existing);
        table.draw(false);
        syncIdCardToFilteredRows();
    }

    async function loadIds(source) {
        var devId = deviceId();
        if (!devId) {
            setStat("Select a device first.", "warn");
            return;
        }
        setStat("Loading IDs…");
        var url =
            "/Device/EnrolledUsers/Ids?source=" +
            encodeURIComponent(source) +
            "&deviceId=" +
            encodeURIComponent(devId);
        var res = await fetch(url, { credentials: "same-origin" });
        var data = await res.json();
        if (!data.ok) {
            setStat(data.error || "Failed to load IDs.", "warn");
            return;
        }
        idQueue = data.ids || [];
        setStat(idQueue.length + " IDs loaded (" + source + ")", idQueue.length ? "ready" : "idle");
        refreshGateosSourceUi();
    }

    async function resolvePastedFromText(text) {
        var res = await fetch("/Device/EnrolledUsers/ResolveIds", {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ text: text }),
        });
        var data = await res.json();
        if (!data.ok) {
            setStat("Could not parse IDs.", "warn");
            return false;
        }
        idQueue = data.ids || [];
        setStat(
            idQueue.length + " IDs queued (normalized NIC)",
            idQueue.length ? "ready" : "warn"
        );
        if (!idQueue.length) setStat("No valid NICs found in the text box.", "warn");
        return idQueue.length > 0;
    }

    async function resolvePasted() {
        var ta = document.getElementById("nicList");
        var text = ta ? String(ta.value || "") : "";
        if (text.trim()) {
            await resolvePastedFromText(text);
            return;
        }
        if (pendingExcelWorkbook) {
            await importNicExcel();
            return;
        }
        setStat("Paste NICs or attach an Excel file first.", "warn");
    }

    async function readApiJson(res) {
        var text = await res.text();
        if (!text) {
            return {
                ok: false,
                error:
                    res.status === 404
                        ? "API not found — rebuild and restart the web app."
                        : "Empty response (HTTP " + res.status + ").",
            };
        }
        try {
            return JSON.parse(text);
        } catch (parseErr) {
            return {
                ok: false,
                error:
                    "Invalid server response (HTTP " +
                    res.status +
                    "). " +
                    (res.status === 404
                        ? "Rebuild and restart the app."
                        : text.replace(/<[^>]+>/g, " ").trim().slice(0, 180)),
            };
        }
    }

    async function pullBatch(ids) {
        var allDev = document.getElementById("chkAllDevices").checked;
        if (!allDev && !deviceId()) {
            return { ok: false, error: "Select a device." };
        }
        var delayMs = 120;
        var body = {
            deviceId: deviceId(),
            allDevices: allDev,
            delayMs: delayMs,
            ids: ids,
        };
        var res = await fetch("/Device/EnrolledUsers/PullBatch", {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
        return res.json();
    }

    async function pullRecentEnrolledFromAllDevices() {
        if (pulling) return;
        var chk = document.getElementById("chkAllDevices");
        if (chk) chk.checked = true;
        pulling = true;
        setExportExcelVisible(false);
        setBulkPhotosVisible(false);
        var gateFetch = document.getElementById("fetchBtn");
        var btnRecent = document.getElementById("btnRecentEnrolled");
        if (gateFetch) gateFetch.disabled = true;
        if (btnRecent) btnRecent.disabled = true;
        setProgress("Reading enrollments for last 3 days on all devices…", null);
        setStat("Pulling users enrolled today and the previous 2 days…", "ready");
        try {
            var res = await fetch("/Device/EnrolledUsers/PullRecentEnrolled", {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json", Accept: "application/json" },
                body: JSON.stringify({
                    allDevices: true,
                    lookbackDays: 3,
                    delayMs: 120,
                }),
            });
            var data = await readApiJson(res);
            if (data.ok === false && !data.employees) {
                setStat(data.error || "Could not load recent enrollments.", "warn");
                setProgress("Stopped: " + (data.error || "request failed"), 0);
                return;
            }
            if (data.employees && data.employees.length) {
                var newRows = rowsFromEmployees(data.employees);
                if (table) {
                    table.clear();
                    table.rows.add(newRows);
                    table.draw(false);
                    syncIdCardToFilteredRows();
                }
                idQueue = data.employees
                    .map(function (e) {
                        return e.employeeId || "";
                    })
                    .filter(Boolean);
                var win = data.windowLabel ? " · " + data.windowLabel : "";
                setStat(data.found + " enrolled in window" + win, "ready");
                var elapsedSec = data.elapsedMs ? Math.round(data.elapsedMs / 1000) + "s" : "";
                setProgress(
                    "Done · " + data.found + " user(s)" + (data.deviceCount ? " · " + data.deviceCount + " device(s)" : "") + (elapsedSec ? " · " + elapsedSec : ""),
                    100
                );
                var swalHtml =
                    "<strong>" +
                    data.found +
                    "</strong> user(s) enrolled in the last 3 days.";
                if (data.windowLabel) swalHtml += "<br><small>" + data.windowLabel + "</small>";
                if (data.deviceCount) swalHtml += "<br><small>Devices scanned: " + data.deviceCount + "</small>";
                setExportExcelVisible(true);
                setBulkPhotosVisible(true);
                swalOperationComplete(swalHtml);
            } else {
                setStat(data.error || "No enrollments in the last 3 days.", "warn");
                setProgress("Stopped: " + (data.error || "no users"), 0);
                if (table) {
                    table.clear().draw();
                }
            }
        } catch (e) {
            setStat("Network error while pulling recent enrollments.", "warn");
            setProgress("Stopped: " + (e && e.message ? e.message : "network error"), 0);
        } finally {
            pulling = false;
            if (gateFetch) gateFetch.disabled = false;
            if (btnRecent) btnRecent.disabled = false;
            refreshGateosSourceUi();
        }
    }

    async function pullAllFromDeviceList() {
        if (pulling) return;
        var allDev = document.getElementById("chkAllDevices").checked;
        if (!allDev && !deviceId()) {
            setStat("Select a device first.", "warn");
            return;
        }
        pulling = true;
        setExportExcelVisible(false);
        setBulkPhotosVisible(false);
        var btn = document.getElementById("btnPullAllDevice");
        var fetchBtn = document.getElementById("btnPull");
        var gateFetch = document.getElementById("fetchBtn");
        if (btn) btn.disabled = true;
        if (fetchBtn) fetchBtn.disabled = true;
        if (gateFetch) gateFetch.disabled = true;
        setProgress("Reading full user list from device…", null);
        setStat("Pulling all enrolled users (no ID list)…", "ready");
        try {
            var res = await fetch("/Device/EnrolledUsers/PullAll", {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    deviceId: deviceId(),
                    allDevices: allDev,
                    delayMs: 120,
                    pushWaitSeconds: 240,
                }),
            });
            var data = await res.json();
            if (data.employees && data.employees.length) {
                var newRows = rowsFromEmployees(data.employees);
                if (table) {
                    table.clear();
                    table.rows.add(newRows);
                    table.draw(false);
                    syncIdCardToFilteredRows();
                }
                idQueue = data.employees.map(function (e) {
                    return e.employeeId || "";
                }).filter(Boolean);
                var method = data.method ? " · " + data.method : "";
                var countHint = "";
                if (data.deviceEnrolledCount > 0 && data.found < data.deviceEnrolledCount) {
                    countHint =
                        " · device reports " + data.deviceEnrolledCount + " enrolled (" + (data.deviceEnrolledCount - data.found) + " missing)";
                }
                setStat(
                    data.found + " users from device" + countHint + method,
                    data.incomplete ? "warn" : "ready"
                );
                var elapsedSec = data.elapsedMs ? Math.round(data.elapsedMs / 1000) + "s" : "";
                setProgress(
                    "Done · " + data.found + " user(s)" + (elapsedSec ? " · " + elapsedSec : "") + countHint,
                    data.incomplete ? 85 : 100
                );
                var swalHtml =
                    "<strong>" +
                    data.found +
                    "</strong> user(s) loaded from the device.";
                if (data.deviceEnrolledCount > 0) {
                    swalHtml += "<br><small>Device enrolled count: <strong>" + data.deviceEnrolledCount + "</strong></small>";
                }
                if (data.incomplete && data.warning) {
                    swalHtml += "<br><small class=\"text-warning\">" + data.warning + "</small>";
                }
                if (method) swalHtml += "<br><small>" + method.replace(/^ ·\s*/, "") + "</small>";
                if (elapsedSec) swalHtml += "<br><small>Time: " + elapsedSec + "</small>";
                if (data.incomplete) {
                    swalHtml +=
                        "<br><small>Enable <strong>Push + User Data</strong> on the terminal (server = portal PC, ports 9920/9924), run Sync/Download on device, then fetch again.</small>";
                }
                swalHtml += "<br><small>Use <strong>Export to Excel</strong> above the grid to download.</small>";
                setExportExcelVisible(true);
                setBulkPhotosVisible(true);
                swalOperationComplete(swalHtml);
            } else {
                setStat(data.error || "No users returned from device.", "warn");
                setProgress("Stopped: " + (data.error || "no users"), 0);
            }
        } catch (e) {
            setStat("Network error while pulling user list.", "warn");
            setProgress("Stopped: network error", 0);
        } finally {
            pulling = false;
            if (btn) btn.disabled = false;
            if (fetchBtn) fetchBtn.disabled = false;
            if (gateFetch) gateFetch.disabled = false;
            refreshGateosSourceUi();
        }
    }

    async function pullAll() {
        if (pulling) return;
        if (!idQueue.length) {
            var queued = await ensureIdQueueFromInputs();
            if (!queued) {
                setStat("Paste NICs, attach Excel, or load IDs first.", "warn");
                return;
            }
        }
        var allDev = document.getElementById("chkAllDevices").checked;
        if (!allDev && !deviceId()) {
            setStat("Select a device first.", "warn");
            return;
        }
        pulling = true;
        setExportExcelVisible(false);
        setBulkPhotosVisible(false);
        var fetchBtn = document.getElementById("btnPull");
        var gateFetch = document.getElementById("fetchBtn");
        if (fetchBtn) fetchBtn.disabled = true;
        if (gateFetch) gateFetch.disabled = true;
        setProgress("Starting…", 0);
        var batchInput = document.getElementById("batchSize");
        var batchSize = parseInt(batchInput && batchInput.value, 10) || 35;
        if (batchSize < 5) batchSize = 5;
        if (batchSize > 80) batchSize = 80;

        var total = idQueue.length;
        var found = 0;
        var offset = 0;
        var stoppedEarly = false;

        try {
            while (offset < total) {
                var slice = idQueue.slice(offset, offset + batchSize);
                offset += slice.length;
                var pct = Math.round((offset / total) * 100);
                setProgress("Fetching " + offset + " / " + total + " IDs…", pct);
                var data = await pullBatch(slice);
                if (data.employees && data.employees.length) {
                    found += data.employees.length;
                    mergeIntoTable(rowsFromEmployees(data.employees));
                }
                if (data.error && !data.ok) {
                    setProgress("Stopped: " + data.error, pct);
                    stoppedEarly = true;
                    break;
                }
            }
            if (!stoppedEarly) {
                setProgress("Done · " + found + " profile(s) on device", 100);
                setExportExcelVisible(true);
                setBulkPhotosVisible(true);
                swalOperationComplete(
                    "<strong>" +
                        found +
                        "</strong> profile(s) loaded from the device.<br><small>" +
                        total +
                        " ID(s) processed in the queue</small><br><small>Use <strong>Export to Excel</strong> above the grid to download.</small>"
                );
            }
        } finally {
            pulling = false;
            if (fetchBtn) fetchBtn.disabled = false;
            if (gateFetch) gateFetch.disabled = false;
            refreshGateosSourceUi();
        }
    }

    function defaultExportFileName() {
        var d = new Date();
        var p = function (n) {
            return n < 10 ? "0" + n : String(n);
        };
        return (
            "device-enrolled-users-" +
            d.getFullYear() +
            p(d.getMonth() + 1) +
            p(d.getDate())
        );
    }

    function buildExportColumnChecks() {
        var host = document.getElementById("exportColumnChecks");
        if (!host || host.dataset.built === "1") return;
        host.dataset.built = "1";
        EXPORT_COLUMNS.forEach(function (col) {
            if (col.id === "sno") return;
            var label = document.createElement("label");
            label.className = "enrolled-export-check";
            var input = document.createElement("input");
            input.type = "checkbox";
            input.className = "export-col-check";
            input.value = col.id;
            input.checked = true;
            label.appendChild(input);
            label.appendChild(document.createTextNode(" " + col.label));
            host.appendChild(label);
        });
    }

    function openExportModal(preferAllRows) {
        buildExportColumnChecks();
        var nameEl = document.getElementById("exportFileName");
        if (nameEl && !nameEl.value.trim()) nameEl.value = defaultExportFileName();
        if (preferAllRows) {
            var allScope = document.querySelector('input[name="exportScope"][value="all"]');
            if (allScope) allScope.checked = true;
        }
        var modal = document.getElementById("enrolledExportModal");
        if (modal) {
            modal.classList.remove("is-hidden");
            document.body.style.overflow = "hidden";
        }
    }

    function closeExportModal() {
        var modal = document.getElementById("enrolledExportModal");
        if (modal) modal.classList.add("is-hidden");
        document.body.style.overflow = "";
    }

    function selectedExportColumns() {
        var cols = [{ id: "sno", label: "S.No" }];
        document.querySelectorAll(".export-col-check:checked").forEach(function (input) {
            var id = input.value;
            EXPORT_COLUMNS.forEach(function (c) {
                if (c.id === id) cols.push({ id: c.id, label: c.label, col: c.col });
            });
        });
        return cols;
    }

    function exportRowsFromGrid() {
        if (!table) return [];
        var scopeEl = document.querySelector('input[name="exportScope"]:checked');
        var scope = scopeEl ? scopeEl.value : "filtered";
        var api =
            scope === "all"
                ? table.rows({ search: "none" })
                : table.rows({ search: "applied" });
        return api.data().toArray();
    }

    function cellForExport(row, colDef, sno) {
        if (colDef.id === "sno") return sno;
        return row[colDef.col] != null ? String(row[colDef.col]) : "";
    }

    function sanitizeFileName(name) {
        var s = String(name || "export").trim() || "export";
        return s.replace(/[^\w\-]+/g, "-").replace(/-+/g, "-").replace(/^-|-$/g, "");
    }

    function downloadBlob(blob, filename) {
        var a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        setTimeout(function () {
            URL.revokeObjectURL(a.href);
            a.remove();
        }, 0);
    }

    function runExport() {
        if (!table) {
            setStat("Load grid data before export.", "warn");
            return;
        }
        var rows = exportRowsFromGrid();
        if (!rows.length) {
            setStat("No rows to export.", "warn");
            return;
        }
        var cols = selectedExportColumns();
        if (cols.length <= 1) {
            setStat("Select at least one column.", "warn");
            return;
        }
        var includeHeader = document.getElementById("exportIncludeHeader");
        var withHeader = !includeHeader || includeHeader.checked;
        var formatEl = document.querySelector('input[name="exportFormat"]:checked');
        var format = formatEl ? formatEl.value : "xlsx";
        var fileBase = sanitizeFileName(
            (document.getElementById("exportFileName") || {}).value || defaultExportFileName()
        );

        var aoa = [];
        if (withHeader) {
            aoa.push(cols.map(function (c) {
                return c.label;
            }));
        }
        rows.forEach(function (row, i) {
            aoa.push(
                cols.map(function (c) {
                    return cellForExport(row, c, i + 1);
                })
            );
        });

        if (format === "csv") {
            var lines = aoa.map(function (line) {
                return line
                    .map(function (cell) {
                        var s = String(cell == null ? "" : cell);
                        if (/[",\r\n]/.test(s)) return '"' + s.replace(/"/g, '""') + '"';
                        return s;
                    })
                    .join(",");
            });
            var bom = "\uFEFF";
            downloadBlob(
                new Blob([bom + lines.join("\r\n")], { type: "text/csv;charset=utf-8" }),
                fileBase + ".csv"
            );
        } else {
            if (!window.XLSX) {
                setStat("Excel library not loaded. Refresh the page.", "warn");
                return;
            }
            var ws = window.XLSX.utils.aoa_to_sheet(aoa);
            var wb = window.XLSX.utils.book_new();
            window.XLSX.utils.book_append_sheet(wb, ws, "Enrolled users");
            window.XLSX.writeFile(wb, fileBase + ".xlsx");
        }
        closeExportModal();
    }

    function bindExportModal() {
        document.querySelectorAll("[data-enrolled-export-close]").forEach(function (el) {
            el.addEventListener("click", closeExportModal);
        });
        var openBtn = document.getElementById("btnExportExcel");
        if (openBtn) {
            openBtn.addEventListener("click", function () {
                openExportModal(true);
            });
        }
        var runBtn = document.getElementById("btnEnrolledExportRun");
        if (runBtn) runBtn.addEventListener("click", runExport);
        document.addEventListener("keydown", function (ev) {
            if (ev.key === "Escape") closeExportModal();
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        initDeviceSelect();
        initGateosSourceUi();
        initTable();
        bindExportModal();
        var bulkBtn = document.getElementById("btnBulkDownloadPhotos");
        if (bulkBtn) bulkBtn.addEventListener("click", bulkDownloadPhotos);
        bindNicExcelImport();
        refreshIssueDate();
        setInterval(refreshIssueDate, 60000);
        document.querySelectorAll(".enrolled-rfid-tab").forEach(function (btn) {
            btn.addEventListener("click", function () {
                setRfidTab(btn.getAttribute("data-rfid-filter") || "assigned");
            });
        });
        if (!isGateosEnrolledPage()) {
            var btnDev = document.getElementById("btnIdsDevice");
            var btnPortal = document.getElementById("btnIdsPortal");
            var btnParse = document.getElementById("btnParseNics");
            var btnPull = document.getElementById("btnPull");
            if (btnDev) btnDev.addEventListener("click", function () { loadIds("device"); });
            if (btnPortal) btnPortal.addEventListener("click", function () { loadIds("portal"); });
            if (btnParse) btnParse.addEventListener("click", function () { resolvePasted(); });
            if (btnPull) btnPull.addEventListener("click", function () { pullAll(); });
            var btnPullAll = document.getElementById("btnPullAllDevice");
            if (btnPullAll) {
                btnPullAll.addEventListener("click", function () {
                    pullAllFromDeviceList();
                });
            }
        }
        var nicTa = document.getElementById("nicList");
        if (nicTa) {
            nicTa.addEventListener("input", function () {
                refreshGateosSourceUi();
            });
        }
        var fileInput = document.getElementById("nicExcelFile");
        if (fileInput) {
            fileInput.addEventListener("change", function () {
                var f = fileInput.files && fileInput.files[0];
                var nameEl = document.getElementById("excelFileName");
                if (nameEl) {
                    if (f) {
                        nameEl.textContent = "Attached: " + f.name;
                        nameEl.hidden = false;
                        nameEl.classList.remove("geu-hidden");
                    } else {
                        nameEl.hidden = true;
                        nameEl.classList.add("geu-hidden");
                    }
                }
            });
        }
    });

    window.geuSetStat = setStat;
    window.geuGetImportRows = function () {
        var fromExcel = extractImportRowsFromWorkbook();
        if (fromExcel.length) return fromExcel;
        var ta = document.getElementById("nicList");
        var text = ta ? String(ta.value || "") : "";
        if (!text.trim()) return [];
        return text
            .split(/\r?\n/)
            .map(function (line, i) {
                return { line: i + 1, employeeId: normalizeClient(line) };
            })
            .filter(function (r) {
                return r.employeeId;
            });
    };
})();
