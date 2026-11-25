(function (window, $) {
    "use strict";
    if (!$ || !$.fn.DataTable) return;

    const DT_LANG = {
        search: "",
        searchPlaceholder: "Search…",
        lengthMenu: "Show _MENU_",
        info: "Showing _START_–_END_ of _TOTAL_ records",
        infoEmpty: "Showing 0 of 0 records",
        infoFiltered: "(filtered from _MAX_ total)",
        zeroRecords: "No matching records",
        paginate: {
            first: "«",
            last: "»",
            next: "›",
            previous: "‹",
        },
        processing: '<span class="dt-processing">Loading…</span>',
    };

    function panelFor(tableEl) {
        return tableEl.closest(".panel-datatable") || tableEl.closest(".panel");
    }

    function scrollAreaFor(tableEl) {
        const wrapper = tableEl.closest(".dataTables_wrapper");
        if (wrapper) {
            const area = wrapper.querySelector(".portal-dt-scroll");
            if (area) return area;
        }
        return tableEl.closest(".portal-dt-host");
    }

    function scrollMaxHeight(tableEl) {
        const panel = panelFor(tableEl);
        const anchor = panel || tableEl;
        const top = anchor.getBoundingClientRect().top;
        const reserve = 96;
        return Math.max(180, window.innerHeight - top - reserve);
    }

    function applyScrollHeight(tableEl) {
        const area = scrollAreaFor(tableEl);
        if (!area) return;
        const wrapper = tableEl.closest(".dataTables_wrapper");
        const panel = panelFor(tableEl);

        if (panel && wrapper) {
            const panelRect = panel.getBoundingClientRect();
            const wrapperTop = wrapper.getBoundingClientRect().top;
            const toolbar = wrapper.querySelector(".dt-toolbar");
            const footer = wrapper.querySelector(".dt-footer");
            let reserved = 12;
            if (toolbar) reserved += toolbar.getBoundingClientRect().height;
            if (footer) reserved += footer.getBoundingClientRect().height;
            const available = panelRect.bottom - wrapperTop - reserved;
            if (available > 100 && panelRect.height > 0) {
                area.style.maxHeight = Math.floor(available) + "px";
                return;
            }
        }

        let chrome = 160;
        if (wrapper) {
            const toolbar = wrapper.querySelector(".dt-toolbar");
            const footer = wrapper.querySelector(".dt-footer");
            chrome += (toolbar?.offsetHeight || 0) + (footer?.offsetHeight || 0);
        }
        const anchor = panel || tableEl;
        const top = anchor.getBoundingClientRect().top;
        area.style.maxHeight = Math.max(120, window.innerHeight - top - chrome) + "px";
    }

    function relayout(tableEl) {
        if (!tableEl) return;
        applyScrollHeight(tableEl);
        if ($.fn.DataTable.isDataTable(tableEl)) {
            $(tableEl).DataTable().columns.adjust();
        }
    }

    function mountGridToolbar(tableEl) {
        const panel = panelFor(tableEl);
        if (!panel) return;
        const add = panel.querySelector(".portal-panel-add[data-grid-toolbar]");
        const wrapper = tableEl.closest(".dataTables_wrapper");
        const toolbar = wrapper && wrapper.querySelector(".dt-toolbar");
        if (!add || !toolbar || add.dataset.mounted === "1") return;
        add.dataset.mounted = "1";
        const slot = document.createElement("div");
        slot.className = "portal-dt-add-slot";
        slot.appendChild(add);
        const filter = toolbar.querySelector(".dataTables_filter");
        if (filter) toolbar.insertBefore(slot, filter);
        else toolbar.appendChild(slot);
    }

    function datetimeColumnDefs(tableEl) {
        const spec = tableEl.getAttribute("data-portal-datetime-cols");
        if (!spec || !window.PortalDateTime) return [];
        return spec
            .split(",")
            .map(function (s) {
                return parseInt(s.trim(), 10);
            })
            .filter(function (n) {
                return !isNaN(n);
            })
            .map(function (idx) {
                return {
                    targets: idx,
                    render: PortalDateTime.dataTablesRender,
                };
            });
    }

    function baseOptions(tableEl, extra) {
        return $.extend(true, {
            autoWidth: false,
            deferRender: true,
            pageLength: 25,
            lengthMenu: [[10, 25, 50, 100, -1], [10, 25, 50, 100, "All"]],
            order: [],
            language: DT_LANG,
            dom: '<"dt-toolbar"lf><"portal-dt-scroll"t><"dt-footer"ip>',
            initComplete: function () {
                const api = this.api();
                const wrapper = $(api.table().container());
                wrapper.addClass("portal-dt-wrapper");
                $(api.table().node()).css("width", "100%");
                applyScrollHeight(tableEl);
                mountGridToolbar(tableEl);
                api.columns.adjust();
            },
        }, extra || {});
    }

    const resizeTimers = new WeakMap();

    function bindResize(api, tableEl) {
        if (!tableEl || resizeTimers.has(tableEl)) return;
        resizeTimers.set(tableEl, true);
        const fn = function () {
            applyScrollHeight(tableEl);
            api.columns.adjust();
        };
        window.addEventListener("resize", fn);
        fn();
    }

    window.PortalDataTable = {
        scrollYFor: scrollMaxHeight,
        relayout: relayout,
        init: function (selector, options) {
            const el = typeof selector === "string" ? document.querySelector(selector) : selector;
            if (!el) return null;
            if ($.fn.DataTable.isDataTable(el)) {
                return $(el).DataTable();
            }
            const opts = baseOptions(el, options);
            const dtCols = datetimeColumnDefs(el);
            if (dtCols.length) {
                opts.columnDefs = (opts.columnDefs || []).concat(dtCols);
            }
            if (options && options.ajax) opts.processing = true;
            if (!options || !options.dom) {
                /* keep default dom with scroll wrapper unless page overrides (e.g. dashboard) */
            }
            const api = $(el).DataTable(opts);
            bindResize(api, el);
            api.on("draw", function () {
                applyScrollHeight(el);
                api.columns.adjust();
            });
            return api;
        },
        initStatic: function (selector, options) {
            return this.init(selector, options);
        },
        reload: function (selector, resetPaging) {
            const el = typeof selector === "string" ? document.querySelector(selector) : selector;
            if (el && $.fn.DataTable.isDataTable(el)) {
                $(el).DataTable().ajax.reload(null, resetPaging !== false);
            }
        },
    };

    $(function () {
        $("table.portal-datatable[data-dt-server!='true']:not([data-dt-manual='true'])").each(function () {
            if ($.fn.DataTable.isDataTable(this)) return;
            const order = this.dataset.dtOrder;
            const opts = {};
            if (order) {
                const parts = order.split(",");
                opts.order = [[parseInt(parts[0], 10), parts[1] || "asc"]];
            }
            opts.columnDefs = (opts.columnDefs || []).concat([
                { orderable: false, searchable: false, targets: -1 },
            ]);
            PortalDataTable.init(this, opts);
            relayout(this);
        });

        $("table.portal-datatable[data-dt-server='true']").each(function () {
            if (!$.fn.DataTable.isDataTable(this)) return;
            relayout(this);
        });
    });
})(window, window.jQuery);
