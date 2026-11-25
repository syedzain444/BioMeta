$(function () {
    const tableEl = document.getElementById("logsTable");
    if (!tableEl) return;

    const table = PortalDataTable.init(tableEl, {
        processing: true,
        serverSide: true,
        order: [[0, "desc"]],
        pageLength: 25,
        ajax: {
            url: "/Logs/Data",
            data: function (d) {
                d.fromDate = $("#fromDate").val();
                d.toDate = $("#toDate").val();
                d.fromTime = $("#fromTime").val();
                d.toTime = $("#toTime").val();
                d.deviceId = $("#filterDeviceId").val() || null;
            },
        },
        columns: [
            { data: 0 },
            { data: 1 },
            { data: 2 },
            { data: 3 },
            { data: 4 },
            { data: 5 },
            { data: 6 },
            { data: 7 },
            { data: 8 },
            { data: 9 },
        ],
        columnDefs: [
            {
                targets: 1,
                render: function (data) {
                    if (!data) return "—";
                    return '<code class="emp-id">' + $("<div>").text(data).html() + "</code>";
                },
            },
        ],
    });

    $("#btnApply").on("click", () => table.ajax.reload());
    $("#btnClear").on("click", () => {
        $("#fromDate, #toDate, #fromTime, #toTime").val("");
        table.search("").draw();
        table.ajax.reload();
    });
});
