(function () {
    "use strict";

    const $ = (s, r) => (r || document).querySelector(s);
    const $$ = (s, r) => [...(r || document).querySelectorAll(s)];
    const pad = (n) => String(n).padStart(2, "0");
    const nF = (n) => Math.round(n).toLocaleString("en-US");

    const GRADS = [
        "linear-gradient(135deg,#10b981,#14b8a6)",
        "linear-gradient(135deg,#f59e0b,#f97316)",
        "linear-gradient(135deg,#6366f1,#818cf8)",
        "linear-gradient(135deg,#ec4899,#f43f5e)",
        "linear-gradient(135deg,#06b6d4,#0ea5e9)",
    ];

    let DEVS = [];
    let ALL_PUNCHES = [];
    let ENR = 467;
    let useSample = false;
    let pin = null;
    let pollTimer = null;

    const st = {
        dev: -1,
        range: "today",
        live: true,
        hide: { i: false, o: false },
    };

    const NOW = new Date();
    const SH = () => Math.max(new Date().getHours(), 10);

    function startOfDay(d) {
        const x = new Date(d);
        x.setHours(0, 0, 0, 0);
        return x;
    }

    function punchIo(p) {
        const s = ((p.status || "") + " " + (p.workCode || "")).toLowerCase();
        if (/\bout\b|exit|check.?out|depart|leave/.test(s)) return "o";
        return "i";
    }

    function methodBucket(source) {
        const s = String(source || "").toLowerCase();
        if (/face|photo|visible/.test(s)) return 0;
        if (/finger|bio|template/.test(s)) return 1;
        if (/card|rfid|tag/.test(s)) return 2;
        return 0;
    }

    function selDevIds() {
        return st.dev < 0 ? DEVS.map((_, i) => i) : [st.dev];
    }

    function normPunch(row) {
        return {
            deviceId: row.deviceId,
            deviceName: row.deviceName || "",
            employeeId: row.employeeId || "",
            employeeName: row.employeeName || "Unknown",
            status: row.status || "",
            workCode: row.workCode || "",
            cardSource: row.cardSource || "",
            _d: new Date(row.punchTime),
            _io: null,
        };
    }

    function inRange(p, dayStart, dayEnd) {
        return p._d >= dayStart && p._d <= dayEnd;
    }

    function punchesFiltered(dayStart, dayEnd, devFilter) {
        return ALL_PUNCHES.filter((p) => {
            if (devFilter >= 0 && p.deviceId !== DEVS[devFilter]?.deviceId) return false;
            return inRange(p, dayStart, dayEnd);
        });
    }

    function aggPeriod(start, end, devFilter) {
        const list = punchesFiltered(start, end, devFilter);
        let I = 0;
        let O = 0;
        let L = 0;
        const H = Array(24).fill(0);
        const present = new Set();
        list.forEach((p) => {
            p._io = p._io || punchIo(p);
            if (p._io === "i") {
                I++;
                if (p._d.getHours() >= 9) L++;
                if (startOfDay(p._d).getTime() === startOfDay(NOW).getTime()) present.add(p.employeeId);
            } else O++;
            H[p._d.getHours()] += 1;
        });
        return { I, O, L, H, present: present.size, list };
    }

    function rangeBounds() {
        const n = st.range === "today" ? 1 : st.range === "7d" ? 7 : 30;
        const end = new Date();
        const start = startOfDay(end);
        start.setDate(start.getDate() - (n - 1));
        const prevEnd = new Date(start);
        prevEnd.setMilliseconds(-1);
        const prevStart = startOfDay(prevEnd);
        prevStart.setDate(prevStart.getDate() - (n - 1));
        return { n, start, end, prevStart, prevEnd, hm: st.range === "today" ? SH() : 23 };
    }

    function cnt(el, v, f) {
        if (!el) return;
        f = f || nF;
        const a = el._v ?? 0;
        el._v = v;
        cancelAnimationFrame(el._r);
        const t0 = performance.now();
        const step = (t) => {
            const p = Math.min(1, (t - t0) / 600);
            const e = 1 - Math.pow(1 - p, 3);
            el.textContent = f(a + (v - a) * e);
            if (p < 1) el._r = requestAnimationFrame(step);
        };
        el._r = requestAnimationFrame(step);
    }

    function spark(id, a) {
        const el = $(id);
        if (!el) return;
        const m = Math.max(...a, 1);
        const n = a.length;
        if (n < 2) {
            el.innerHTML = "";
            return;
        }
        const p = a.map((v, i) => {
            const y = Math.max(4, Math.min(32, 32 - (v / m) * 24));
            return [((i / (n - 1)) * 100).toFixed(2), y.toFixed(2)];
        });
        const d = "M" + p.map((q) => q.join(",")).join("L");
        el.innerHTML =
            `<path d="${d}L100,34L0,34Z" fill="rgba(255,255,255,.22)" stroke="none"/>` +
            `<path d="${d}" fill="none" stroke="rgba(255,255,255,.9)" stroke-width="1.6" vector-effect="non-scaling-stroke"/>`;
    }

    function delta(id, c, p, good) {
        good = good !== undefined ? good : 1;
        const e = $(id);
        if (!e) return;
        if (!p) {
            e.textContent = "—";
            return;
        }
        const x = ((c - p) / p) * 100;
        e.textContent = (x >= 0 ? "▲ " : "▼ ") + Math.abs(x).toFixed(1) + "% vs prev.";
        e.style.background = x * good >= 0 ? "rgba(255,255,255,.26)" : "rgba(15,23,42,.28)";
    }

    function smooth(pts, yMin, yMax) {
        if (pts.length < 2) return "";
        const clampY = (y) => Math.max(yMin, Math.min(yMax, y));
        let d = `M${pts[0][0]},${pts[0][1]}`;
        for (let i = 0; i < pts.length - 1; i++) {
            const a = pts[i - 1] || pts[i];
            const b = pts[i];
            const c = pts[i + 1];
            const e = pts[i + 2] || c;
            d += `C${b[0] + (c[0] - a[0]) / 6},${clampY(b[1] + (c[1] - a[1]) / 6)} ${c[0] - (e[0] - b[0]) / 6},${clampY(c[1] - (e[1] - b[1]) / 6)} ${c[0]},${c[1]}`;
        }
        return d;
    }

    function series() {
        const b = rangeBounds();
        const n = b.n;
        const L = [];
        const I = [];
        const O = [];
        const T = [];
        if (n === 1) {
            for (let h = 5; h <= b.hm; h++) {
                let i = 0;
                let o = 0;
                selDevIds().forEach((k) => {
                    const devId = DEVS[k]?.deviceId;
                    ALL_PUNCHES.forEach((p) => {
                        if (p.deviceId !== devId && st.dev >= 0) return;
                        if (startOfDay(p._d).getTime() !== startOfDay(NOW).getTime()) return;
                        if (p._d.getHours() !== h) return;
                        const io = punchIo(p);
                        if (io === "i") i++;
                        else o++;
                    });
                });
                L.push(pad(h) + ":00");
                I.push(i);
                O.push(o);
                T.push(h >= 9 ? i : 0);
            }
        } else {
            for (let d = n - 1; d >= 0; d--) {
                const day = startOfDay(NOW);
                day.setDate(day.getDate() - d);
                const dayEnd = new Date(day);
                dayEnd.setHours(23, 59, 59, 999);
                let i = 0;
                let o = 0;
                let l = 0;
                selDevIds().forEach((k) => {
                    const a = aggPeriod(day, dayEnd, k);
                    i += a.I;
                    o += a.O;
                    l += a.L;
                });
                L.push(day.toLocaleDateString("en-GB", { day: "numeric", month: "short" }));
                I.push(i);
                O.push(o);
                T.push(l);
            }
        }
        return { n, L, I, O, T };
    }

    function kpis(s) {
        const b = rangeBounds();
        let cI = 0;
        let cO = 0;
        let cL = 0;
        let cH = Array(24).fill(0);
        let cPresent = 0;
        selDevIds().forEach((k) => {
            const a = aggPeriod(b.start, b.end, k);
            cI += a.I;
            cO += a.O;
            cL += a.L;
            a.H.forEach((v, h) => {
                cH[h] += v;
            });
            if (b.n === 1) cPresent += a.present;
        });
        let pI = 0;
        let pO = 0;
        let pL = 0;
        let pPresent = 0;
        selDevIds().forEach((k) => {
            const a = aggPeriod(b.prevStart, b.prevEnd, k);
            pI += a.I;
            pO += a.O;
            pL += a.L;
            if (b.n === 1) pPresent += a.present;
        });

        cnt($("#v1"), cI + cO);
        delta("#d1", cI + cO, pI + pO);
        spark("#s1", s.I.map((v, i) => v + s.O[i]));

        $("#l2").textContent = b.n === 1 ? "Present today" : "Avg present / day";
        let avgPresent = cPresent;
        if (b.n > 1) {
            const byDay = new Map();
            ALL_PUNCHES.forEach((p) => {
                if (p._d < b.start || p._d > b.end) return;
                if (st.dev >= 0 && p.deviceId !== DEVS[st.dev].deviceId) return;
                if (punchIo(p) !== "i") return;
                const dk = startOfDay(p._d).getTime();
                if (!byDay.has(dk)) byDay.set(dk, new Set());
                byDay.get(dk).add(p.employeeId);
            });
            const days = [...byDay.values()];
            avgPresent = days.length ? Math.round(days.reduce((s, set) => s + set.size, 0) / days.length) : 0;
        }
        cnt($("#v2"), b.n === 1 ? Math.min(ENR, cPresent) : avgPresent);
        delta("#d2", b.n === 1 ? cPresent : avgPresent, b.n === 1 ? pPresent : pI / Math.max(b.n, 1));
        spark("#s2", s.I);

        cnt($("#v3"), cL);
        delta("#d3", cL, pL, -1);
        spark("#s3", s.T);

        const pk = cH.indexOf(Math.max(...cH));
        $("#v4").textContent = pad(pk) + ":00";
        $("#d4").textContent = nF(cH[pk]) + " punches";
        spark("#s4", cH.slice(5, 22));

        const rangeLabel =
            { today: "Today", "7d": "Last 7 days", "30d": "Last 30 days" }[st.range] || st.range;
        $("#sub").textContent =
            (st.dev < 0 ? "All devices" : DEVS[st.dev]?.n || "Device") +
            " · " +
            rangeLabel +
            " · " +
            nF(ENR) +
            " enrolled users";
    }

    let TR;
    const W = 720;
    const H = 288;
    const P = { l: 54, r: 18, t: 22, b: 36 };

    function chartCeiling(values) {
        const mx = Math.max(1, ...values);
        const exp = Math.pow(10, Math.floor(Math.log10(mx)));
        const n = mx / exp;
        let nice = 10;
        if (n <= 1) nice = 1;
        else if (n <= 2) nice = 2;
        else if (n <= 5) nice = 5;
        return nice * exp * 1.15;
    }

    function trend(draw) {
        const s = series();
        const m = chartCeiling([...s.I, ...s.O]);
        const n = s.L.length;
        const plotL = P.l;
        const plotR = W - P.r;
        const plotT = P.t + 12;
        const plotB = H - P.b - 4;
        const X = (i) => plotL + (plotR - plotL) * (n < 2 ? 0.5 : i / (n - 1));
        const Y = (v) => plotB - (Math.min(v, m) / m) * (plotB - plotT);
        const yBase = plotB;

        let h =
            `<defs>` +
            `<clipPath id="govPlotClip"><rect x="${plotL}" y="${plotT - 6}" width="${plotR - plotL}" height="${plotB - plotT + 12}" rx="4"/></clipPath>` +
            `<linearGradient id="govGi" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stop-color="#10b981" stop-opacity=".42"/><stop offset="85%" stop-color="#10b981" stop-opacity=".08"/><stop offset="100%" stop-color="#10b981" stop-opacity="0"/></linearGradient>` +
            `<linearGradient id="govGo" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stop-color="#6366f1" stop-opacity=".32"/><stop offset="85%" stop-color="#6366f1" stop-opacity=".06"/><stop offset="100%" stop-color="#6366f1" stop-opacity="0"/></linearGradient>` +
            `<filter id="govLineGlow" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="0" dy="1" stdDeviation="1.2" flood-color="#10b981" flood-opacity=".25"/></filter>` +
            `</defs>`;

        h += `<rect x="${plotL}" y="${plotT}" width="${plotR - plotL}" height="${plotB - plotT}" fill="#f8fafc" rx="10" stroke="#eef2f7"/>`;
        h += `<g class="gov-chart-grid" aria-hidden="true">`;
        for (let i = 0; i <= 4; i++) {
            const v = (m * i) / 4;
            const y = Y(v);
            h += `<line class="gov-grid-line" x1="${plotL}" x2="${plotR}" y1="${y}" y2="${y}"/>`;
            h += `<text class="gov-axis-y" x="${plotL - 10}" y="${y + 4}">${nF(v)}</text>`;
        }
        const xStep = n <= 7 ? 1 : n <= 14 ? 2 : Math.max(1, Math.ceil(n / 8));
        s.L.forEach((l, i) => {
            if (i % xStep !== 0 && i !== n - 1) return;
            h += `<text class="gov-axis-x" x="${X(i)}" y="${H - 12}">${l}</text>`;
        });
        h += `</g>`;

        h += `<g clip-path="url(#govPlotClip)">`;
        for (const [k, c, gd] of [
            ["o", "#6366f1", "govGo"],
            ["i", "#10b981", "govGi"],
        ]) {
            if (st.hide[k]) continue;
            const pts = s[k.toUpperCase()].map((v, i) => [X(i), Y(v)]);
            const d = smooth(pts, plotT - 20, plotB + 20);
            if (!d) continue;
            const glow = k === "i" ? ' filter="url(#govLineGlow)"' : "";
            h += `<path class="gov-chart-area" d="${d}L${X(n - 1)},${yBase}L${X(0)},${yBase}Z" fill="url(#${gd})" stroke="none"/>`;
            h += `<path class="gov-chart-line gov-chart-line-${k}" pathLength="1" d="${d}" fill="none" stroke="${c}" stroke-width="2.6"${glow}/>`;
        }
        h += `</g>`;
        h += `<g id="cr" style="display:none"><line id="crL" y1="${plotT}" y2="${plotB}" stroke="#94a3b8" stroke-dasharray="4 4"/><circle id="crI" r="5" fill="#fff" stroke="#10b981" stroke-width="2.5"/><circle id="crO" r="5" fill="#fff" stroke="#6366f1" stroke-width="2.5"/></g>`;
        h += `<rect id="hit" x="${plotL}" y="${P.t}" width="${plotR - plotL}" height="${H - P.t}" fill="transparent" stroke="none"/>`;

        const sv = $("#trend");
        sv.innerHTML = h;
        $("#cw").classList.toggle("draw", !!draw);
        TR = { s, X, Y, n, plotT, plotB };
        const totalIn = s.I.reduce((a, b) => a + b, 0);
        const totalOut = s.O.reduce((a, b) => a + b, 0);
        const trS = $("#trS");
        if (trS) {
            trS.innerHTML =
                `<span class="gov-tr-in"><i></i>${nF(totalIn)} in</span>` +
                `<span class="gov-tr-out"><i></i>${nF(totalOut)} out</span>`;
        }

        const hit = $("#hit");
        const tip = $("#tip");
        if (!hit) return;
        hit.onmousemove =
            hit.ontouchmove =
            (e) => {
                const t = e.touches ? e.touches[0] : e;
                const r = sv.getBoundingClientRect();
                const x = ((t.clientX - r.left) / r.width) * W;
                const i = Math.max(0, Math.min(n - 1, Math.round(((x - P.l) / (W - P.l - P.r)) * (n - 1))));
                const cx = X(i);
                $("#cr").style.display = "";
                $("#crL").setAttribute("x1", cx);
                $("#crL").setAttribute("x2", cx);
                for (const [id, v, k] of [
                    ["crI", s.I[i], "i"],
                    ["crO", s.O[i], "o"],
                ]) {
                    const c = $("#" + id);
                    c.setAttribute("cx", cx);
                    c.setAttribute("cy", Y(v));
                    c.style.display = st.hide[k] ? "none" : "";
                }
                tip.innerHTML =
                    `<b>${s.L[i]}</b>` +
                    (st.hide.i
                        ? ""
                        : `<span><i style="background:#34d399"></i>Check-ins <b style="margin:0 0 0 auto;padding-left:12px">${nF(s.I[i])}</b></span>`) +
                    (st.hide.o
                        ? ""
                        : `<span><i style="background:#818cf8"></i>Check-outs <b style="margin:0 0 0 auto;padding-left:12px">${nF(s.O[i])}</b></span>`);
                tip.style.left = Math.max(14, Math.min(86, (cx / W) * 100)) + "%";
                tip.style.opacity = 1;
            };
        hit.onmouseleave = () => {
            tip.style.opacity = 0;
            $("#cr").style.display = "none";
        };
    }

    function devices() {
        const n = st.range === "today" ? 1 : st.range === "7d" ? 7 : 30;
        const start = startOfDay(NOW);
        start.setDate(start.getDate() - (n - 1));
        const end = new Date();
        const tot = DEVS.map((d) => {
            let t = 0;
            ALL_PUNCHES.forEach((p) => {
                if (p.deviceId !== d.deviceId) return;
                if (p._d >= start && p._d <= end) t++;
            });
            return t;
        });
        const mx = Math.max(...tot, 1);
        $("#devs").innerHTML = DEVS.map(
            (d, k) =>
                `<button type="button" class="dev" data-k="${k}" aria-pressed="${st.dev === k}"><span class="di" style="background:${d.c}"><svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg></span><span class="m"><b>${d.n}</b> <span class="st ${d.s === "Online" ? "ok" : ""}">${d.s}</span><div class="bt"><i style="width:${(tot[k] / mx) * 100}%;background:${d.c}"></i></div></span><span class="n">${nF(tot[k])}</span></button>`
        ).join("");
        $$(".dev").forEach((b) => {
            b.onclick = () => setDev(st.dev === +b.dataset.k ? -1 : +b.dataset.k);
        });
    }

    function heat() {
        const S = Array.from({ length: 7 }, () => Array(24).fill(0));
        const C = Array(7).fill(0);
        for (let d = 0; d < 28; d++) {
            const day = startOfDay(NOW);
            day.setDate(day.getDate() - d);
            const w = (day.getDay() + 6) % 7;
            C[w]++;
            const dayEnd = new Date(day);
            dayEnd.setHours(23, 59, 59, 999);
            ALL_PUNCHES.forEach((p) => {
                if (p._d < day || p._d > dayEnd) return;
                if (st.dev >= 0 && p.deviceId !== DEVS[st.dev].deviceId) return;
                S[w][p._d.getHours()]++;
            });
        }
        const A = S.map((r, w) => r.map((v) => v / (C[w] || 1)));
        const mx = Math.max(...A.flat(), 1);
        const N = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        let h = "<span></span>";
        for (let x = 6; x <= 22; x++) h += `<span class="h">${x % 3 === 0 ? x : ""}</span>`;
        N.forEach((n, w) => {
            h += `<span class="r">${n}</span>`;
            for (let x = 6; x <= 22; x++) {
                const v = A[w][x];
                h += `<div class="c ${pin === w + "-" + x ? "pin" : ""}" data-w="${w}" data-h="${x}" style="background:rgba(20,184,166,${(0.07 + (v / mx) * 0.93).toFixed(2)})"></div>`;
            }
        });
        const g = $("#hg");
        g.innerHTML = h;
        const info = (w, x) =>
            `${N[w]} ${pad(x)}:00 – ${pad(x + 1)}:00 · avg <b>${Math.round(A[w][x])}</b> punches`;
        const df = "Hover a cell to see details, click to pin it.";
        $$(".c", g).forEach((c) => {
            c.onmouseenter = () => ($("#hInfo").innerHTML = info(+c.dataset.w, +c.dataset.h));
            c.onmouseleave = () => ($("#hInfo").innerHTML = pin ? info(...pin.split("-").map(Number)) : df);
            c.onclick = () => {
                const k = c.dataset.w + "-" + c.dataset.h;
                pin = pin === k ? null : k;
                $$(".c", g).forEach((z) => z.classList.toggle("pin", z.dataset.w + "-" + z.dataset.h === pin));
            };
        });
        if (pin) $("#hInfo").innerHTML = info(...pin.split("-").map(Number));
    }

    function donut() {
        const n = st.range === "today" ? 1 : st.range === "7d" ? 7 : 30;
        const start = startOfDay(NOW);
        start.setDate(start.getDate() - (n - 1));
        const M = [0, 0, 0];
        ALL_PUNCHES.forEach((p) => {
            if (p._d < start) return;
            if (st.dev >= 0 && p.deviceId !== DEVS[st.dev].deviceId) return;
            M[methodBucket(p.cardSource)]++;
        });
        const T = M.reduce((a, b) => a + b, 0) || 1;
        const labels = ["Face", "Fingerprint", "Card"];
        const C = ["#10b981", "#6366f1", "#f59e0b"];
        const r = 52;
        const L = 2 * Math.PI * r;
        let acc = 0;
        let h = "";
        M.forEach((v, j) => {
            const l = (v / T) * L;
            h += `<circle data-j="${j}" cx="75" cy="75" r="${r}" stroke="${C[j]}" stroke-width="18" stroke-dasharray="${Math.max(0, l - 3)} ${L - Math.max(0, l - 3)}" stroke-dashoffset="${-acc}"/>`;
            acc += l;
        });
        $("#dn").innerHTML = h;
        const dc = $("#dc");
        const def = () => (dc.innerHTML = `<b>${nF(T)}</b><small>punches</small>`);
        def();
        $("#dl").innerHTML = M.map(
            (v, j) => `<div data-j="${j}"><i style="background:${C[j]}"></i>${labels[j]}<span>${((v / T) * 100).toFixed(1)}%</span></div>`
        ).join("");
        $$("#dn circle,#dl div").forEach((e) => {
            e.onmouseenter = () => {
                const j = +e.dataset.j;
                dc.innerHTML = `<b>${((M[j] / T) * 100).toFixed(0)}%</b><small>${labels[j]}</small>`;
                $$("#dn circle").forEach((c) => {
                    c.style.opacity = +c.dataset.j === j ? 1 : 0.3;
                    c.style.strokeWidth = +c.dataset.j === j ? 22 : 18;
                });
            };
            e.onmouseleave = () => {
                def();
                $$("#dn circle").forEach((c) => {
                    c.style.opacity = 1;
                    c.style.strokeWidth = 18;
                });
            };
        });
    }

    function feedItem(p) {
        const io = punchIo(p);
        const dev = DEVS.find((d) => d.deviceId === p.deviceId) || DEVS[0] || { n: p.deviceName, c: GRADS[0] };
        const nm = p.employeeName || "Unknown";
        const li = document.createElement("li");
        li.innerHTML =
            `<span class="a" style="background:${dev.c}">${nm.split(" ").map((w) => w[0]).join("").slice(0, 2)}</span>` +
            `<span class="m">${nm}<small>${p.employeeId} · ${dev.n || p.deviceName}</small></span>` +
            `<span class="t ${io}">${io === "i" ? "Check-in" : "Check-out"}</span>` +
            (window.PortalDateTime
                ? `<span class="portal-dt-when portal-dt-when--stack">${PortalDateTime.formatHtml(p.punchTime || p._d)}</span>`
                : `<time>${pad(p._d.getHours())}:${pad(p._d.getMinutes())}:${pad(p._d.getSeconds())}</time>`);
        return li;
    }

    function renderFeed() {
        const f = $("#feed");
        if (!f) return;
        const list = [...ALL_PUNCHES]
            .sort((a, b) => b._d - a._d)
            .slice(0, 6);
        f.innerHTML = "";
        list.forEach((p) => f.appendChild(feedItem(p)));
    }

    function renderAll(draw, skipHeat) {
        const s = series();
        kpis(s);
        trend(draw);
        devices();
        if (!skipHeat) heat();
        donut();
        renderFeed();
        $("#ttl").textContent = st.dev < 0 ? "All devices" : DEVS[st.dev]?.n || "Device";
        $("#sb1").innerHTML = `<b>${st.dev < 0 ? "All devices" : DEVS[st.dev]?.n}</b> · ${({ today: "Today", "7d": "7 days", "30d": "30 days" }[st.range] || st.range)}`;
        $("#fS").textContent = st.live ? "live" : "paused";
    }

    function setDev(v) {
        st.dev = v;
        $$("#segDev button").forEach((b) => b.setAttribute("aria-pressed", +b.dataset.v === v));
        renderAll();
    }

    function setRange(v) {
        st.range = v;
        $$("#segRange button").forEach((b) => b.setAttribute("aria-pressed", b.dataset.v === v));
        renderAll();
    }

    function setLive(v) {
        st.live = v;
        const liveEl = $("#live");
        if (liveEl) liveEl.checked = v;
        $("#pill").classList.toggle("on", v);
        $("#pillT").textContent = v ? "Receiving punches" : "Paused";
        $("#fS").textContent = v ? "live" : "paused";
    }

    function buildSegDev() {
        const seg = $("#segDev");
        if (!seg) return;
        let html = `<button type="button" aria-pressed="true" data-v="-1">All</button>`;
        DEVS.forEach((d, i) => {
            html += `<button type="button" aria-pressed="false" data-v="${i}">${d.n}</button>`;
        });
        seg.innerHTML = html;
        $$("#segDev button").forEach((b) => (b.onclick = () => setDev(+b.dataset.v)));
    }

    function exportCsv() {
        const n = st.range === "today" ? 1 : st.range === "7d" ? 7 : 30;
        const start = startOfDay(NOW);
        start.setDate(start.getDate() - (n - 1));
        const rows = [["Date", "Device", "Employee ID", "Name", "Type", "Source"]];
        ALL_PUNCHES.forEach((p) => {
            if (p._d < start) return;
            if (st.dev >= 0 && p.deviceId !== DEVS[st.dev].deviceId) return;
            const dev = DEVS.find((d) => d.deviceId === p.deviceId);
            rows.push([
                p._d.toISOString().slice(0, 10),
                dev?.n || p.deviceName,
                p.employeeId,
                p.employeeName,
                punchIo(p) === "i" ? "Check-in" : "Check-out",
                p.cardSource,
            ]);
        });
        const a = document.createElement("a");
        a.href = URL.createObjectURL(new Blob([rows.map((x) => x.map((c) => `"${String(c).replace(/"/g, '""')}"`).join(",")).join("\n")], { type: "text/csv" }));
        a.download = "attendance-overview.csv";
        a.click();
        URL.revokeObjectURL(a.href);
    }

    function seedSample() {
        useSample = true;
        const rng = () => {
            let a = 0;
            return () => {
                a = (a + 0x6d2b79f5) | 0;
                let t = Math.imul(a ^ (a >>> 15), 1 | a);
                t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
                return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
            };
        };
        const R = rng();
        DEVS = [
            { deviceId: 1, n: "Gate 1", s: "Push idle", c: GRADS[0], enrolled: 467 },
            { deviceId: 2, n: "Gate 2", s: "Online", c: GRADS[1], enrolled: 120 },
            { deviceId: 3, n: "Gate 3", s: "Online", c: GRADS[2], enrolled: 80 },
        ];
        ENR = 467;
        const names = ["Ahmed Raza", "Sana Khan", "Bilal Ahmed", "Hina Malik", "Usman Ali"];
        ALL_PUNCHES = [];
        for (let d = 0; d < 30; d++) {
            for (let h = 6; h <= 18; h++) {
                const count = Math.floor(R() * 8);
                for (let i = 0; i < count; i++) {
                    const day = startOfDay(NOW);
                    day.setDate(day.getDate() - d);
                    day.setHours(h, Math.floor(R() * 60), Math.floor(R() * 60));
                    ALL_PUNCHES.push(
                        normPunch({
                            punchTime: day.toISOString(),
                            deviceId: DEVS[Math.floor(R() * DEVS.length)].deviceId,
                            deviceName: "Gate",
                            employeeId: "42201" + String(Math.floor(R() * 1e8)).padStart(8, "0"),
                            employeeName: names[Math.floor(R() * names.length)],
                            status: R() < 0.5 ? "Check-in" : "Check-out",
                            workCode: "",
                            cardSource: "Face",
                        })
                    );
                }
            }
        }
        $("#dataChip").textContent = "Sample data";
        $("#dataChip").classList.remove("live");
        $("#sbFoot").textContent = "Sample data — connect your API";
    }

    async function loadData() {
        try {
            const res = await fetch("/Dashboard/OverviewData", { credentials: "same-origin" });
            const data = await res.json();
            if (!data.ok) throw new Error("bad response");
            DEVS = (data.devices || []).map((d, i) => ({
                deviceId: d.deviceId,
                n: d.name,
                s: d.status || "Inactive",
                c: GRADS[i % GRADS.length],
                enrolled: d.enrolled || 0,
            }));
            if (!DEVS.length) {
                seedSample();
                return;
            }
            ENR = Math.max(DEVS.reduce((s, d) => s + (d.enrolled || 0), 0), 1);
            ALL_PUNCHES = (data.punches || []).map(normPunch);
            useSample = !data.hasLiveData;
            const chip = $("#dataChip");
            if (chip) {
                chip.textContent = data.hasLiveData ? "Live data" : "Limited history";
                chip.classList.toggle("live", !!data.hasLiveData);
            }
            $("#sbFoot").textContent = data.hasLiveData
                ? `${nF(data.total || 0)} records in portal`
                : "Add punches to populate charts";
        } catch {
            seedSample();
        }
    }

    function startPoll() {
        if (pollTimer) clearInterval(pollTimer);
        pollTimer = setInterval(async () => {
            if (!st.live) return;
            await loadData();
            buildSegDev();
            renderAll(false, true);
        }, 12000);
    }

    function startSignalR() {
        if (!window.signalR) return;
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/attendance", { withCredentials: true })
            .withAutomaticReconnect()
            .build();
        connection.on("NewPunches", async () => {
            if (!st.live) return;
            await loadData();
            buildSegDev();
            renderAll(false, true);
        });
        connection.start().catch(() => {});
    }

    const CMD = [];
    function rebuildCmd() {
        CMD.length = 0;
        CMD.push(["Range: Today", "Time range", () => setRange("today")]);
        CMD.push(["Range: Last 7 days", "Time range", () => setRange("7d")]);
        CMD.push(["Range: Last 30 days", "Time range", () => setRange("30d")]);
        CMD.push(["Device: All devices", "Filter", () => setDev(-1)]);
        DEVS.forEach((d, k) => CMD.push(["Device: " + d.n, "Filter", () => setDev(k)]));
        CMD.push(["Toggle live feed", "Action", () => setLive(!st.live)]);
        CMD.push(["Export CSV", "Action", exportCsv]);
    }

    let si = 0;
    let fl = CMD;
    const ov = $("#ov");
    const sq = $("#sq");

    function sRender() {
        $("#sl").innerHTML =
            fl.map((c, i) => `<li class="${i === si ? "s" : ""}" data-i="${i}">${c[0]}<small>${c[1]}</small></li>`).join("") ||
            "<li>No matches</li>";
        $$("#sl li").forEach((l) => {
            l.onclick = () => run(+l.dataset.i);
        });
    }

    function openSpot() {
        ov.hidden = false;
        sq.value = "";
        fl = CMD;
        si = 0;
        sRender();
        sq.focus();
    }

    function closeSpot() {
        ov.hidden = true;
    }

    function run(i) {
        const c = fl[i];
        if (c) {
            closeSpot();
            c[2]();
        }
    }

    async function init() {
        if (ov) ov.hidden = true;

        const name = window.govDisplayName || "Portal Administrator";
        const hr = NOW.getHours();
        $("#hi").textContent =
            (hr < 12 ? "Good morning" : hr < 17 ? "Good afternoon" : "Good evening") + ", " + name.split(" ")[0];

        await loadData();
        buildSegDev();
        st.dev = -1;
        rebuildCmd();
        renderAll(true);

        $$("#segRange button").forEach((b) => (b.onclick = () => setRange(b.dataset.v)));
        $("#live").onchange = (e) => setLive(e.target.checked);
        $("#exp").onclick = exportCsv;
        $$(".lg").forEach((b) => {
            b.onclick = () => {
                const k = b.dataset.s;
                st.hide[k] = !st.hide[k];
                b.setAttribute("aria-pressed", !st.hide[k]);
                trend(false);
            };
        });

        $("#spBtn").onclick = openSpot;
        sq.oninput = () => {
            const q = sq.value.toLowerCase();
            fl = CMD.filter((c) => c[0].toLowerCase().includes(q) || c[1].toLowerCase().includes(q));
            si = 0;
            sRender();
        };
        sq.onkeydown = (e) => {
            if (e.key === "ArrowDown") {
                si = Math.min(fl.length - 1, si + 1);
                sRender();
                e.preventDefault();
            } else if (e.key === "ArrowUp") {
                si = Math.max(0, si - 1);
                sRender();
                e.preventDefault();
            } else if (e.key === "Enter") run(si);
        };
        ov.onclick = (e) => {
            if (e.target === ov) closeSpot();
        };
        document.addEventListener("keydown", (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
                e.preventDefault();
                ov.hidden ? openSpot() : closeSpot();
            } else if (e.key === "Escape") closeSpot();
        });

        startSignalR();
        startPoll();
    }

    if (document.body.classList.contains("gateos-page-overview")) {
        init();
    }
})();
