using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RoofingLeadGeneration.Data.Models;

namespace RoofingLeadGeneration.Services
{
    /// <summary>
    /// Generates the informational hail damage PDF report for a single lead.
    /// This is a door-knocker leave-behind, NOT a certified meteorological assessment.
    /// </summary>
    public class HailReportService
    {
        // Brand palette
        private static readonly string BrandOrange  = "#f97316";
        private static readonly string NavyDark     = "#0f172a";
        private static readonly string SlateLight   = "#94a3b8";
        private static readonly string SlateText    = "#334155";
        private static readonly string White        = "#ffffff";

        public HailReportService()
        {
            // QuestPDF Community licence — free for open-source / small commercial use
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public byte[] Generate(Lead lead, string generatedBy)
        {
            var riskColor = lead.RiskLevel switch
            {
                "High"   => "#ef4444",
                "Medium" => "#f97316",
                "Low"    => "#22c55e",
                _        => "#64748b"
            };

            var riskDescription = lead.RiskLevel switch
            {
                "High"   => "Significant hail impact detected. Hail size and storm intensity indicate a high probability of roof damage requiring professional inspection.",
                "Medium" => "Moderate hail impact detected. Depending on roof age and material, damage may be present. A professional inspection is recommended.",
                "Low"    => "Minor hail detected in the area. Impact to the roof is possible but less likely. A visual inspection is still advisable.",
                _        => "Hail was detected in the vicinity of this property during the recorded storm event."
            };

            var hailSizeText = lead.HailSize ?? "Not recorded";
            var stormDate    = lead.LastStormDate ?? "Not recorded";
            var generatedOn  = DateTime.Now.ToString("MMMM d, yyyy 'at' h:mm tt");

            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(0);
                    page.DefaultTextStyle(x => x.FontFamily("Helvetica"));

                    // ── Header bar ───────────────────────────────────────────
                    page.Header().Element(header =>
                    {
                        header.Background(NavyDark).Padding(0).Column(col =>
                        {
                            // Orange accent strip
                            col.Item().Background(BrandOrange).Height(6);

                            col.Item().Padding(28).Row(row =>
                            {
                                row.RelativeItem().Column(inner =>
                                {
                                    inner.Item().Text("HAIL DAMAGE REPORT")
                                        .FontSize(22).Bold().FontColor(White);
                                    inner.Item().Text("Informational Property Assessment")
                                        .FontSize(11).FontColor(SlateLight);
                                });
                                row.ConstantItem(160).AlignRight().AlignMiddle().Column(inner =>
                                {
                                    inner.Item().AlignRight().Text("Storm")
                                        .FontSize(18).Bold().FontColor(White);
                                    inner.Item().AlignRight().Text(t =>
                                    {
                                        t.Span("Lead").FontColor(BrandOrange).FontSize(18).Bold();
                                        t.Span(" Pro").FontColor(White).FontSize(18).Bold();
                                    });
                                });
                            });
                        });
                    });

                    // ── Body ────────────────────────────────────────────────
                    page.Content().Padding(32).Column(col =>
                    {
                        col.Spacing(20);

                        // ── Property address card ────────────────────────
                        col.Item().Background("#f8fafc").Border(1).BorderColor("#e2e8f0")
                            .Padding(20).Column(inner =>
                        {
                            inner.Item().Text("PROPERTY ADDRESS")
                                .FontSize(9).Bold().FontColor(SlateLight)
                                .LetterSpacing(0.08f);
                            inner.Item().PaddingTop(6).Text(lead.Address)
                                .FontSize(16).Bold().FontColor(NavyDark);
                            if (lead.Lat.HasValue && lead.Lng.HasValue)
                            {
                                inner.Item().PaddingTop(4).Text(
                                    $"GPS: {lead.Lat:F5}, {lead.Lng:F5}")
                                    .FontSize(9).FontColor(SlateLight);
                            }
                        });

                        // ── Storm event + risk row ───────────────────────
                        col.Item().Row(row =>
                        {
                            row.Spacing(16);

                            // Storm details
                            row.RelativeItem().Background("#f8fafc").Border(1).BorderColor("#e2e8f0")
                                .Padding(20).Column(inner =>
                            {
                                inner.Item().Text("STORM EVENT")
                                    .FontSize(9).Bold().FontColor(SlateLight)
                                    .LetterSpacing(0.08f);
                                inner.Item().PaddingTop(12).Row(r =>
                                {
                                    r.RelativeItem().Column(c =>
                                    {
                                        c.Item().Text("Storm Date").FontSize(9).FontColor(SlateLight);
                                        c.Item().PaddingTop(2).Text(stormDate)
                                            .FontSize(13).Bold().FontColor(NavyDark);
                                    });
                                    r.RelativeItem().Column(c =>
                                    {
                                        c.Item().Text("Hail Size").FontSize(9).FontColor(SlateLight);
                                        c.Item().PaddingTop(2).Text(hailSizeText)
                                            .FontSize(13).Bold().FontColor(NavyDark);
                                    });
                                });
                                if (!string.IsNullOrWhiteSpace(lead.EstimatedDamage))
                                {
                                    inner.Item().PaddingTop(12).Row(r =>
                                    {
                                        r.RelativeItem().Column(c =>
                                        {
                                            c.Item().Text("Est. Damage").FontSize(9).FontColor(SlateLight);
                                            c.Item().PaddingTop(2).Text(lead.EstimatedDamage)
                                                .FontSize(13).Bold().FontColor(NavyDark);
                                        });
                                    });
                                }
                            });

                            // Risk level
                            row.ConstantItem(160).Background(riskColor)
                                .Padding(20).AlignCenter().Column(inner =>
                            {
                                inner.Item().AlignCenter().Text("RISK LEVEL")
                                    .FontSize(9).Bold().FontColor(White);
                                inner.Item().PaddingTop(12).AlignCenter()
                                    .Text(lead.RiskLevel ?? "Unknown")
                                    .FontSize(26).Bold().FontColor(White);
                                inner.Item().PaddingTop(6).AlignCenter()
                                    .Text("Damage Probability")
                                    .FontSize(9).FontColor(White).LineHeight(1.4f);
                            });
                        });

                        // ── Risk explanation ─────────────────────────────
                        col.Item().Border(1).BorderColor(riskColor)                            .Padding(16).Row(r =>
                        {
                            r.ConstantItem(4).Background(riskColor);
                            r.ConstantItem(12);
                            r.RelativeItem().Column(inner =>
                            {
                                inner.Item().Text("What This Means")
                                    .FontSize(10).Bold().FontColor(NavyDark);
                                inner.Item().PaddingTop(4).Text(riskDescription)
                                    .FontSize(10).FontColor(SlateText).LineHeight(1.5f);
                            });
                        });

                        // ── Data source ──────────────────────────────────
                        col.Item().Background("#f1f5f9").Padding(14)
                            .Column(inner =>
                        {
                            inner.Item().Text("DATA SOURCE")
                                .FontSize(8).Bold().FontColor(SlateLight)
                                .LetterSpacing(0.08f);
                            inner.Item().PaddingTop(4).Text(
                                "Storm event and hail data sourced from NOAA National Centers for " +
                                "Environmental Information (NCEI) storm event records and radar-derived " +
                                "hail swath analysis. Property parcel data via Regrid.")
                                .FontSize(9).FontColor(SlateText).LineHeight(1.5f);
                        });

                        // ── Disclaimer ───────────────────────────────────
                        col.Item().Background("#fef3c7").Border(1).BorderColor("#fcd34d")
                            .Padding(14).Column(inner =>
                        {
                            inner.Item().Text("IMPORTANT DISCLAIMER")
                                .FontSize(8).Bold().FontColor("#92400e")
                                .LetterSpacing(0.08f);
                            inner.Item().PaddingTop(4).Text(
                                "This is an informational report only. It is based on publicly available " +
                                "NOAA storm data and is NOT a certified meteorological assessment. It does " +
                                "not constitute a formal inspection, an insurance claim, or a guarantee of " +
                                "damage. A licensed roofing contractor and/or insurance adjuster should " +
                                "physically inspect the property to determine actual damage.")
                                .FontSize(9).FontColor("#78350f").LineHeight(1.5f);
                        });
                    });

                    // ── Footer ───────────────────────────────────────────
                    page.Footer().Background(NavyDark).Padding(16).Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text(t =>
                            {
                                t.Span("Storm").FontColor(White).FontSize(10).Bold();
                                t.Span("Lead").FontColor(BrandOrange).FontSize(10).Bold();
                                t.Span(" Pro").FontColor(White).FontSize(10).Bold();
                            });
                            col.Item().PaddingTop(2).Text($"Generated {generatedOn}")
                                .FontSize(8).FontColor(SlateLight);
                        });
                        row.ConstantItem(200).AlignRight().AlignMiddle().Column(col =>
                        {
                            col.Item().AlignRight().Text($"Prepared for: {generatedBy}")
                                .FontSize(8).FontColor(SlateLight);
                            col.Item().AlignRight().PaddingTop(2).Text("stormlead.pro")
                                .FontSize(8).FontColor(BrandOrange);
                        });
                    });
                });
            });

            return doc.GeneratePdf();
        }
    }
}
