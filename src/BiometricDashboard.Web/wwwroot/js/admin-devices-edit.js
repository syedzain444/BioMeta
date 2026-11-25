(function () {
    "use strict";

    const form = document.querySelector(".gateos-device-form");
    if (!form) return;

    const testBtn = document.getElementById("btnDeviceTestDraft");
    const panel = document.getElementById("deviceEditTestPanel");
    const serialInput = document.getElementById("LastTestSerial");
    const testUrl = window.adminDevicesTestDraftUrl || "/Admin/Devices/TestDraft";
    const TEST_TIMEOUT_MS = 180000;

    function token() {
        return form.querySelector('input[name="__RequestVerificationToken"]')?.value || "";
    }

    function esc(s) {
        return String(s ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

    function renderResult(data) {
        if (!panel) return;
        const protocolOk = !!data.online;
        const head = protocolOk
            ? '<p class="gateos-test-ok"><i class="bi bi-check-circle-fill"></i> Device answered HDCP.</p>'
            : data.tcpOk
              ? '<p class="gateos-test-warn"><i class="bi bi-exclamation-triangle-fill"></i> TCP OK — HDCP failed.</p>'
              : '<p class="gateos-test-fail"><i class="bi bi-x-circle-fill"></i> Connection test failed.</p>';
        panel.innerHTML =
            head +
            '<p class="tiny">' +
            esc(data.message || data.error || "") +
            "</p>" +
            (data.serial ? '<p class="tiny">Serial: <code>' + esc(data.serial) + "</code></p>" : "");
        panel.hidden = false;

        if (serialInput && data.serial) serialInput.value = data.serial;
    }

    async function runTest() {
        if (!panel) return;
        const ip = form.querySelector('[name="DeviceIp"]')?.value?.trim();
        const port = form.querySelector('[name="DevicePort"]')?.value?.trim() || "9922";
        if (!ip) {
            panel.hidden = false;
            panel.innerHTML = '<p class="gateos-test-fail">Enter device IP first.</p>';
            return;
        }

        panel.hidden = false;
        panel.innerHTML =
            '<p class="gateos-test-loading"><span class="gateos-test-spin"></span> Testing TCP → GetDeviceInfo → GetRecord…</p>';

        if (testBtn) {
            testBtn.disabled = true;
            testBtn.classList.add("is-busy");
        }

        const ac = new AbortController();
        const timer = setTimeout(() => ac.abort(), TEST_TIMEOUT_MS);

        try {
            const fd = new FormData();
            fd.append("__RequestVerificationToken", token());
            fd.append("deviceIp", ip);
            fd.append("devicePort", port);

            const res = await fetch(testUrl, {
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
                panel.innerHTML =
                    '<p class="gateos-test-fail">Invalid response (HTTP ' + esc(res.status) + "). Refresh and retry.</p>";
                return;
            }
            if (!res.ok) {
                panel.innerHTML =
                    '<p class="gateos-test-fail">' + esc(data.error || data.message || "Request failed") + "</p>";
                return;
            }
            renderResult(data);
        } catch (e) {
            const msg =
                e && e.name === "AbortError" ? "Test timed out (3 min)." : e?.message || "Network error.";
            panel.innerHTML = '<p class="gateos-test-fail">' + esc(msg) + "</p>";
        } finally {
            clearTimeout(timer);
            if (testBtn) {
                testBtn.disabled = false;
                testBtn.classList.remove("is-busy");
            }
        }
    }

    testBtn?.addEventListener("click", (e) => {
        e.preventDefault();
        runTest();
    });

    form.addEventListener("submit", () => {
        const saveBtn = form.querySelector(".gateos-btn-save");
        if (saveBtn) {
            saveBtn.disabled = true;
            saveBtn.dataset.saving = "1";
        }
    });
})();
