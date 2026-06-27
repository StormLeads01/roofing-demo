using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RoofingLeadGeneration.Data.Models;
using System.IO;

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

        private static string HailSizeReference(double sizeInches) => sizeInches switch
        {
            < 0.75 => "Pea",
            < 0.88 => "Penny",
            < 1.00 => "Nickel",
            < 1.25 => "Quarter",
            < 1.50 => "Half Dollar",
            < 1.75 => "Ping Pong Ball",
            < 2.00 => "Golf Ball",
            < 2.50 => "Hen Egg",
            < 2.75 => "Tennis Ball",
            < 4.00 => "Baseball",
            _      => "Softball"
        };

        private static string FormatSource(string source) => source switch
        {
            "lsr"      => "LSR",
            "lsr-wind" => "LSR",
            "tomorrow" => "Tomorrow.io",
            _          => "NOAA"
        };

        public byte[] Generate(
            Lead lead,
            string generatedBy,
            IReadOnlyList<RealDataService.HailEvent>? hailHistory = null,
            IReadOnlyList<RealDataService.WindEvent>?  windHistory = null,
            Data.Models.Org? org = null,
            byte[]? logoBytes = null,
            byte[]? mapBytes  = null)
        {
            // ── Branding resolution ───────────────────────────────────────
            var companyName  = !string.IsNullOrWhiteSpace(org?.CompanyName)  ? org!.CompanyName!  : "StormLead Pro";
            var companyPhone = org?.Phone;
            var companyWeb   = !string.IsNullOrWhiteSpace(org?.Website)       ? org!.Website!      : "stormlead.pro";
            var companyEmail = org?.CompanyEmail;
            var tagline      = org?.Tagline;
            var licenseNo    = org?.LicenseNumber;
            var accentHex    = !string.IsNullOrWhiteSpace(org?.AccentColor)   ? org!.AccentColor!  : BrandOrange;
            var headerHex    = !string.IsNullOrWhiteSpace(org?.HeaderColor)   ? org!.HeaderColor!  : NavyDark;

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
                        header.Background(headerHex).Padding(0).Column(col =>
                        {
                            // Bold accent strip at top — brand color
                            col.Item().Background(accentHex).Height(10);

                            col.Item().Padding(24).Row(row =>
                            {
                                // Left: logo if available, else accent bar placeholder
                                if (logoBytes != null && logoBytes.Length > 0)
                                {
                                    row.ConstantItem(80).AlignMiddle()
                                        .Height(52).Image(logoBytes).FitHeight();
                                    row.ConstantItem(16); // spacer
                                }

                                // Title block
                                row.RelativeItem().AlignMiddle().Column(inner =>
                                {
                                    inner.Item().Text("HAIL DAMAGE REPORT")
                                        .FontSize(20).Bold().FontColor(accentHex);
                                    inner.Item().PaddingTop(3).Text("Informational Property Assessment")
                                        .FontSize(10).FontColor(SlateLight);
                                });

                                // Right: company name + website
                                row.ConstantItem(160).AlignRight().AlignMiddle().Column(inner =>
                                {
                                    inner.Item().AlignRight().Text(companyName)
                                        .FontSize(13).Bold().FontColor(White);
                                    if (!string.IsNullOrWhiteSpace(companyWeb))
                                        inner.Item().AlignRight().PaddingTop(3).Text(companyWeb)
                                            .FontSize(8).FontColor(SlateLight);
                                });
                            });

                            // Bottom accent line
                            col.Item().Background(accentHex).Height(2);
                        });
                    });

                    // ── Body ────────────────────────────────────────────────
                    page.Content().Padding(32).Column(col =>
                    {
                        col.Spacing(20);

                        // ── Property address card ────────────────────────
                        col.Item().Background("#f8fafc").Border(1).BorderColor("#e2e8f0")
                            .Row(row =>
                        {
                            // Address info (left)
                            row.RelativeItem().Padding(20).Column(inner =>
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

                            // Map image (right) — only shown when available
                            if (mapBytes != null && mapBytes.Length > 0)
                            {
                                row.ConstantItem(220).Height(110)
                                    .Image(mapBytes).FitArea();
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

                        // ── Storm History — hail events table ────────────
                        if (hailHistory != null && hailHistory.Count > 0)
                        {
                            col.Item().Column(inner =>
                            {
                                inner.Item().Text("STORM HISTORY — LAST 5 YEARS")
                                    .FontSize(9).Bold().FontColor(SlateLight)
                                    .LetterSpacing(0.08f);
                                inner.Item().PaddingTop(2).Text(
                                    $"{hailHistory.Count} hail event{(hailHistory.Count == 1 ? "" : "s")} recorded within 2 miles of this property.")
                                    .FontSize(9).FontColor(SlateText);

                                inner.Item().PaddingTop(8).Table(table =>
                                {
                                    table.ColumnsDefinition(cols =>
                                    {
                                        cols.RelativeColumn(3);  // Date
                                        cols.RelativeColumn(2);  // Size
                                        cols.RelativeColumn(3);  // Reference
                                        cols.RelativeColumn(2);  // Source
                                    });

                                    table.Header(h =>
                                    {
                                        static void HdrCell(IContainer c, string text) =>
                                            c.Background("#e2e8f0").Padding(5)
                                             .Text(text).FontSize(8).Bold().FontColor("#475569");

                                        HdrCell(h.Cell(), "Date");
                                        HdrCell(h.Cell(), "Hail Size");
                                        HdrCell(h.Cell(), "Size Reference");
                                        HdrCell(h.Cell(), "Source");
                                    });

                                    for (int i = 0; i < hailHistory.Count; i++)
                                    {
                                        var e  = hailHistory[i];
                                        var bg = i % 2 == 0 ? White : "#f8fafc";
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(e.Date.ToString("MMM d, yyyy"))
                                            .FontSize(9).FontColor(NavyDark);
                                        table.Cell().Background(bg).Padding(5)
                                            .Text($"{e.SizeInches:F2}\"")
                                            .FontSize(9).Bold().FontColor(BrandOrange);
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(HailSizeReference(e.SizeInches))
                                            .FontSize(9).FontColor(SlateText);
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(FormatSource(e.Source))
                                            .FontSize(8).FontColor(SlateLight);
                                    }
                                });
                            });
                        }
                        else if (hailHistory != null)
                        {
                            col.Item().Column(inner =>
                            {
                                inner.Item().Text("STORM HISTORY — LAST 5 YEARS")
                                    .FontSize(9).Bold().FontColor(SlateLight)
                                    .LetterSpacing(0.08f);
                                inner.Item().PaddingTop(4).Text(
                                    "No hail events were found within 2 miles of this property in the last 5 years.")
                                    .FontSize(9).FontColor(SlateText).Italic();
                            });
                        }

                        // ── Wind History ─────────────────────────────────
                        if (windHistory != null && windHistory.Count > 0)
                        {
                            col.Item().Column(inner =>
                            {
                                inner.Item().Text("WIND EVENTS — LAST 12 MONTHS")
                                    .FontSize(9).Bold().FontColor(SlateLight)
                                    .LetterSpacing(0.08f);

                                inner.Item().PaddingTop(8).Table(table =>
                                {
                                    table.ColumnsDefinition(cols =>
                                    {
                                        cols.RelativeColumn(4);  // Date
                                        cols.RelativeColumn(3);  // Speed
                                        cols.RelativeColumn(3);  // Source
                                    });

                                    table.Header(h =>
                                    {
                                        static void HdrCell(IContainer c, string text) =>
                                            c.Background("#e2e8f0").Padding(5)
                                             .Text(text).FontSize(8).Bold().FontColor("#475569");

                                        HdrCell(h.Cell(), "Date");
                                        HdrCell(h.Cell(), "Gust Speed");
                                        HdrCell(h.Cell(), "Source");
                                    });

                                    for (int i = 0; i < windHistory.Count; i++)
                                    {
                                        var w  = windHistory[i];
                                        var bg = i % 2 == 0 ? White : "#f8fafc";
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(w.Date.ToString("MMM d, yyyy"))
                                            .FontSize(9).FontColor(NavyDark);
                                        table.Cell().Background(bg).Padding(5)
                                            .Text($"{(int)Math.Round(w.SpeedMph)} mph")
                                            .FontSize(9).Bold().FontColor("#0ea5e9");
                                        table.Cell().Background(bg).Padding(5)
                                            .Text(FormatSource(w.Source))
                                            .FontSize(8).FontColor(SlateLight);
                                    }
                                });
                            });
                        }

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
                    page.Footer().Background(headerHex).Padding(16).Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text(companyName)
                                .FontSize(10).Bold().FontColor(White);
                            if (!string.IsNullOrWhiteSpace(tagline))
                                col.Item().PaddingTop(1).Text(tagline)
                                    .FontSize(7).FontColor(SlateLight).Italic();
                            if (!string.IsNullOrWhiteSpace(licenseNo))
                                col.Item().PaddingTop(1).Text($"Lic# {licenseNo}")
                                    .FontSize(7).FontColor(SlateLight);
                            col.Item().PaddingTop(2).Text($"Generated {generatedOn}")
                                .FontSize(7).FontColor(SlateLight);
                        });
                        row.ConstantItem(220).AlignRight().AlignMiddle().Column(col =>
                        {
                            if (!string.IsNullOrWhiteSpace(companyPhone))
                                col.Item().AlignRight().PaddingTop(2).Text(companyPhone)
                                    .FontSize(8).FontColor(SlateLight);
                            if (!string.IsNullOrWhiteSpace(companyEmail))
                                col.Item().AlignRight().PaddingTop(1).Text(companyEmail)
                                    .FontSize(7).FontColor(SlateLight);
                            col.Item().AlignRight().PaddingTop(2).Text(companyWeb)
                                .FontSize(8).FontColor(accentHex);
                        });
                    });
                });
            });

            return doc.GeneratePdf();
        }
    }
}
