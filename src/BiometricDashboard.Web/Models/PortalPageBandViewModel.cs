namespace BiometricDashboard.Web.Models;

public sealed class PortalPageBandViewModel
{
    public string Eyebrow { get; set; } = "";
    public string Title { get; set; } = "";
    public string IconClass { get; set; } = "bi-grid";
    public string? Subtitle { get; set; }
    public string? SubtitleMuted { get; set; }
    /// <summary>When set, subtitle line uses &lt;em id="..."&gt; for live updates (e.g. device label).</summary>
    public string? SubtitleEmElementId { get; set; }
    public string? SubtitleEmText { get; set; }
    public IReadOnlyList<PortalStatCardViewModel> Stats { get; set; } = [];
    public string? PrimaryActionText { get; set; }
    public string? PrimaryActionUrl { get; set; }
    public string PrimaryActionIcon { get; set; } = "bi-plus-lg";
}

public sealed class PortalStatCardViewModel
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Meta { get; set; }
    public string IconClass { get; set; } = "bi-circle";
    public int ColorIndex { get; set; } = 1;
    public string? ValueElementId { get; set; }
    public string? MetaElementId { get; set; }
}
