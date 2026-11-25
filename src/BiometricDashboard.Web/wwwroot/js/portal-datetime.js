(function (window) {
    "use strict";

    var ISO_PREFIX =
        /^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?/;

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function parse(value) {
        if (value == null || value === "") return null;
        if (value instanceof Date) return isNaN(value.getTime()) ? null : value;
        var raw = String(value).trim();
        var suffixIdx = raw.indexOf(" · ");
        var iso = suffixIdx >= 0 ? raw.slice(0, suffixIdx).trim() : raw;
        var m = iso.match(ISO_PREFIX);
        if (m) {
            var dt = new Date(
                +m[1],
                +m[2] - 1,
                +m[3],
                +(m[4] || 0),
                +(m[5] || 0),
                +(m[6] || 0)
            );
            return isNaN(dt.getTime()) ? null : dt;
        }
        var d = new Date(iso);
        return isNaN(d.getTime()) ? null : d;
    }

    function formatDate(d) {
        return d.toLocaleDateString("en-US", {
            month: "long",
            day: "numeric",
            year: "numeric",
        });
    }

    function formatTime(d) {
        return d.toLocaleTimeString("en-US", {
            hour: "numeric",
            minute: "2-digit",
            second: "2-digit",
            hour12: true,
        });
    }

    function suffixFromRaw(value) {
        var raw = String(value || "");
        var ix = raw.indexOf(" · ");
        return ix >= 0 ? raw.slice(ix + 3) : "";
    }

    function formatHtml(value, options) {
        options = options || {};
        var d = value instanceof Date ? value : parse(value);
        if (!d) {
            var t = String(value || "").trim();
            return t ? escapeHtml(t) : "—";
        }
        var suffix = options.allowSuffix === false ? "" : suffixFromRaw(value);
        var extra = suffix
            ? '<span class="portal-dt-suffix">' + escapeHtml(suffix) + "</span>"
            : "";
        return (
            '<span class="portal-dt-when">' +
            '<span class="portal-dt-date"><i class="bi bi-calendar-event" aria-hidden="true"></i> ' +
            escapeHtml(formatDate(d)) +
            "</span>" +
            '<span class="portal-dt-time"><i class="bi bi-clock" aria-hidden="true"></i> ' +
            escapeHtml(formatTime(d)) +
            "</span>" +
            extra +
            "</span>"
        );
    }

    function dataTablesRender(data, type) {
        if (type === "sort" || type === "type") {
            var d = parse(data);
            return d ? d.getTime() : 0;
        }
        if (type === "filter") {
            var fd = parse(data);
            if (!fd) return data || "";
            return formatDate(fd) + " " + formatTime(fd);
        }
        return formatHtml(data);
    }

    function enhanceRoot(root) {
        if (!root) return;
        root.querySelectorAll("[data-portal-datetime]").forEach(function (el) {
            var raw = el.getAttribute("data-portal-datetime") || el.textContent;
            el.innerHTML = formatHtml(raw, { allowSuffix: false });
            el.removeAttribute("data-portal-datetime");
        });
    }

    window.PortalDateTime = {
        parse: parse,
        formatDate: formatDate,
        formatTime: formatTime,
        formatHtml: formatHtml,
        dataTablesRender: dataTablesRender,
        sortKey: function (v) {
            var d = parse(v);
            return d ? d.getTime() : 0;
        },
        enhanceRoot: enhanceRoot,
    };

    document.addEventListener("DOMContentLoaded", function () {
        enhanceRoot(document);
    });
})(window);
