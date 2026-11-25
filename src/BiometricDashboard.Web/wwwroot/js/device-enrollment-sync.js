(function () {
    "use strict";

    var preview = null;
    var pollTimer = null;
    var compareTimer = null;

    function $(id) {
        return document.getElementById(id);
    }

    function valNum(id) {
        var v = parseInt($(id).value, 10);
        return v > 0 ? v : 0;
    }

    function setStep(n) {
        document.querySelectorAll(".des-step").forEach(function (el) {
            var s = parseInt(el.getAttribute("data-step"), 10);
            el.classList.toggle("des-step-on", s === n);
            el.classList.toggle("des-step-done", s < n);
        });
        for (var i = 1; i <= 4; i++) {
            var p = $("desPanel" + i);
            if (!p) continue;
            if (i === 1) {
                p.classList.add("des-panel-active");
                p.hidden = n !== 1;
            } else {
                p.hidden = n !== i;
            }
        }
    }

    function overlay(show, title, sub, pct) {
        var o = $("desOverlay");
        if (!o) return;
        o.hidden = !show;
        if ($("desLoaderTitle")) $("desLoaderTitle").textContent = title || "Working…";
        if ($("desLoaderSub")) $("desLoaderSub").textContent = sub || "";
        if ($("desLoaderLog")) $("desLoaderLog").textContent = "";
        if ($("desProgressBar")) $("desProgressBar").style.width = (pct != null ? pct : 0) + "%";
    }

    function stopCompareTimer() {
        if (compareTimer) {
            clearInterval(compareTimer);
            compareTimer = null;
        }
    }

    async function readJson(res) {
        var text = await res.text();
        try {
            return JSON.parse(text);
        } catch (e) {
            return { ok: false, error: "Invalid server response (HTTP " + res.status + ")" };
        }
    }

    async function postJson(url, body) {
        var res = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", Accept: "application/json" },
            body: JSON.stringify(body || {}),
        });
        return readJson(res);
    }

    function updateCompareEnabled() {
        var s = valNum("desSource");
        var t = valNum("desTarget");
        var btn = $("desBtnCompare");
        if (btn) btn.disabled = !(s && t && s !== t);
    }

    function visibleRows() {
        if (!preview || !preview.rows) return [];
        var faceOnly = $("desFaceOnly") && $("desFaceOnly").checked;
        return preview.rows.filter(function (r) {
            return !faceOnly || r.hasFace;
        });
    }

    function renderTable() {
        var tb = $("desTbody");
        if (!tb) return;
        tb.innerHTML = "";
        var rows = visibleRows();
        rows.forEach(function (r) {
            var tr = document.createElement("tr");
            tr.innerHTML =
                '<td><input type="checkbox" class="des-row-chk" data-id="' +
                (r.employeeId || "") +
                '" checked /></td><td>' +
                (r.employeeId || "") +
                "</td><td>" +
                (r.name || "—") +
                "</td><td>" +
                (r.department || "—") +
                "</td><td class=\"" +
                (r.hasFace ? "des-badge-ok" : "des-badge-warn") +
                '">' +
                (r.hasFace ? "Yes" : "No") +
                "</td>";
            tb.appendChild(tr);
        });
        var syncBtn = $("desBtnSync");
        if (syncBtn) syncBtn.disabled = rows.length === 0;
    }

    function selectedIds() {
        var ids = [];
        document.querySelectorAll(".des-row-chk:checked").forEach(function (el) {
            var id = el.getAttribute("data-id");
            if (id) ids.push(id);
        });
        return ids;
    }

    async function runCompare() {
        var sourceId = valNum("desSource");
        var targetId = valNum("desTarget");
        if (!sourceId || !targetId || sourceId === targetId) return;

        overlay(true, "Comparing devices", "Reading user lists from source and target…", 5);
        var t0 = Date.now();
        stopCompareTimer();
        compareTimer = setInterval(function () {
            var sec = Math.floor((Date.now() - t0) / 1000);
            overlay(true, "Comparing devices", "This may take several minutes per device (" + sec + "s)", 10 + Math.min(sec, 40));
        }, 1000);

        try {
            var data = await postJson("/Device/EnrollmentSync/Preview", {
                sourceDeviceId: sourceId,
                targetDeviceId: targetId,
                pushWaitSeconds: 240,
                delayMs: 120,
            });
            stopCompareTimer();
            overlay(false);
            if (!data.ok) {
                if ($("desStatus1")) $("desStatus1").textContent = data.error || "Compare failed";
                alert(data.error || "Compare failed");
                return;
            }
            preview = data;
            $("desStSource").textContent = data.source.count;
            $("desStTarget").textContent = data.target.count;
            $("desStMissing").textContent = data.missingOnTarget;
            $("desStBoth").textContent = data.alreadyOnTarget;
            var dupDetails = $("desDupDetails");
            var dupList = $("desDupList");
            var dupCount = $("desDupCount");
            if (dupDetails && dupList && data.alreadyOnTarget > 0) {
                dupDetails.hidden = false;
                if (dupCount) dupCount.textContent = data.alreadyOnTarget;
                dupList.innerHTML = "";
                (data.alreadyOnBothRows || []).forEach(function (r) {
                    var li = document.createElement("li");
                    li.textContent = (r.employeeId || "") + " · " + (r.name || "—");
                    dupList.appendChild(li);
                });
                if (data.alreadyOnTarget > (data.alreadyOnBothRows || []).length) {
                    var more = document.createElement("li");
                    more.textContent =
                        "… and " +
                        (data.alreadyOnTarget - (data.alreadyOnBothRows || []).length) +
                        " more (not copied)";
                    dupList.appendChild(more);
                }
            } else if (dupDetails) dupDetails.hidden = true;
            var w = $("desPreviewWarn");
            if (w) {
                if (data.warning) {
                    w.textContent = data.warning;
                    w.hidden = false;
                } else w.hidden = true;
            }
            setStep(2);
        } catch (e) {
            stopCompareTimer();
            overlay(false);
            alert("Network error: " + (e.message || "compare failed"));
        }
    }

    function stopPoll() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function pollJob(jobId) {
        stopPoll();
        var pollFailures = 0;
        var pollIntervalMs = 1500;
        pollTimer = setInterval(async function () {
            try {
                var res = await fetch("/Device/EnrollmentSync/Job/" + jobId, {
                    credentials: "same-origin",
                    headers: { Accept: "application/json" },
                });
                var data = await readJson(res);
                if (!res.ok || !data.ok || !data.job) {
                    pollFailures++;
                    if (pollFailures >= 8) {
                        stopPoll();
                        overlay(false);
                        alert(
                            "Lost connection to sync status (" +
                                (data.error || res.status) +
                                "). Refresh the page and check Devices or try again."
                        );
                    }
                    return;
                }
                pollFailures = 0;
                var j = data.job;
                var pct = j.progress != null ? j.progress : 0;
                overlay(true, "Syncing to target", j.state, pct);
                if ($("desLoaderLog")) $("desLoaderLog").textContent = j.log || "";
                if ($("desProgressBar")) $("desProgressBar").style.width = pct + "%";
                if (j.state === "Completed" || j.state === "Failed" || j.state === "Cancelled") {
                    var waitingSummary =
                        (j.uploaded || 0) > 0 && !j.compareAfter && (j.log || "").indexOf("Refreshing comparison") >= 0;
                    if (waitingSummary) {
                        overlay(true, "Comparison summary", j.log || "Refreshing lists…", 95);
                        return;
                    }
                    stopPoll();
                    overlay(false);
                    showResult(j);
                    setStep(4);
                }
            } catch (e) {
                /* keep polling */
            }
        }, 1500);
    }

    function renderMiniStats(el, c) {
        if (!el || !c) {
            if (el) el.textContent = "—";
            return;
        }
        el.innerHTML =
            "<span>On source</span> <b>" +
            c.sourceCount +
            "</b><span>On target</span> <b>" +
            c.targetCount +
            "</b><span>Missing on target</span> <b>" +
            c.missingOnTarget +
            "</b><span>On both</span> <b>" +
            c.alreadyOnBoth +
            "</b>";
    }

    function showResult(job) {
        var card = $("desResultCard");
        var up = job.uploaded || 0;
        var fail = job.failed || (job.lines ? job.lines.filter(function (l) { return !l.succeeded; }).length : 0);
        var ok = job.state === "Completed" && up > 0 && fail === 0;
        var partial = up > 0 && fail > 0;
        if (card) card.classList.toggle("is-fail", !ok && !partial);
        $("desResultTitle").textContent = ok
            ? "Synced " + up + " user(s) to " + (job.deviceName || "target")
            : partial
              ? "Partial sync: " + up + " OK, " + fail + " failed"
              : "Sync failed";
        var detail = [job.log, job.error].filter(Boolean).join(" — ");
        if (job.skippedAlreadyOnTarget > 0) {
            detail += (detail ? " · " : "") + job.skippedAlreadyOnTarget + " duplicate(s) blocked";
        }
        $("desResultLog").textContent = detail || "No users uploaded.";
        if (!ok && job.error) alert("Sync failed:\n\n" + job.error);
        var summary = $("desSummaryCompare");
        var before = job.compareBefore || (preview && preview.compare);
        var after = job.compareAfter;
        if (summary && (before || after)) {
            summary.hidden = false;
            $("desSummarySub").textContent =
                "After copy, lists were refreshed on both devices. Missing on target should decrease; on both should increase — no duplicate employee IDs.";
            renderMiniStats($("desBeforeStats"), before);
            renderMiniStats($("desAfterStats"), after);
        } else if (summary) summary.hidden = true;
        var ul = $("desResultLines");
        ul.innerHTML = "";
        (job.lines || []).slice(0, 40).forEach(function (l) {
            var li = document.createElement("li");
            var status = l.verified ? "verified on target" : l.succeeded ? "uploaded (not verified)" : "fail";
            li.textContent =
                (l.employeeId || "") +
                " · " +
                status +
                (l.error ? " — " + l.error : "");
            ul.appendChild(li);
        });
    }

    async function runSync() {
        var ids = selectedIds();
        if (!ids.length) {
            alert("Select at least one user.");
            return;
        }
        if (!preview) return;

        overlay(true, "Starting sync", "Queueing " + ids.length + " user(s)…", 2);
        try {
            var data = await postJson("/Device/EnrollmentSync/Start", {
                sourceDeviceId: preview.source.deviceId,
                targetDeviceId: preview.target.deviceId,
                employeeIds: ids,
                delayMs: 120,
                pushWaitSeconds: 240,
                saveToRegistry: true,
                skipTargetRosterCheck: true,
                compareBefore: preview.compare || {
                    sourceCount: preview.source.count,
                    targetCount: preview.target.count,
                    missingOnTarget: preview.missingOnTarget,
                    alreadyOnBoth: preview.alreadyOnTarget,
                },
            });
            if (!data.ok) {
                overlay(false);
                alert(data.error || "Could not start sync");
                return;
            }
            if (data.skippedDuplicates > 0) {
                alert(
                    data.skippedDuplicates +
                        " user(s) were already on the target device and were not copied (no duplicates)."
                );
            }
            overlay(true, "Syncing", "Reading face data from source…", 8);
            pollJob(data.jobId);
        } catch (e) {
            overlay(false);
            alert("Network error: " + (e.message || "start failed"));
        }
    }

    function init() {
        ["desSource", "desTarget"].forEach(function (id) {
            var el = $(id);
            if (el) el.addEventListener("change", updateCompareEnabled);
        });
        updateCompareEnabled();

        var compare = $("desBtnCompare");
        if (compare) compare.addEventListener("click", runCompare);

        $("desBack1") &&
            $("desBack1").addEventListener("click", function () {
                setStep(1);
            });
        $("desToSelect") &&
            $("desToSelect").addEventListener("click", function () {
                renderTable();
                setStep(3);
            });
        $("desBack2") &&
            $("desBack2").addEventListener("click", function () {
                setStep(2);
            });

        var chkAll = $("desChkAll");
        if (chkAll) {
            chkAll.addEventListener("change", function () {
                document.querySelectorAll(".des-row-chk").forEach(function (el) {
                    el.checked = chkAll.checked;
                });
            });
        }
        $("desFaceOnly") &&
            $("desFaceOnly").addEventListener("change", function () {
                renderTable();
            });

        $("desBtnSync") && $("desBtnSync").addEventListener("click", runSync);
        $("desAnother") &&
            $("desAnother").addEventListener("click", function () {
                preview = null;
                setStep(1);
            });

        setStep(1);
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
})();