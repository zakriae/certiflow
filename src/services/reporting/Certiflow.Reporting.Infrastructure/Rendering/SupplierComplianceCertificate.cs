using System.Globalization;
using Certiflow.Reporting.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Certiflow.Reporting.Infrastructure.Rendering;

/// <summary>
/// The supplier compliance certificate (FR-6.1).
/// <para>
/// This is the document a buyer forwards to an auditor, so it is written to survive being read by
/// someone with no access to the system: every claim on it is either a fact with its evidence
/// beside it, or a status with the date it was true. The verification hash in the footer is what
/// turns it from a printout into an attestation.
/// </para>
/// </summary>
internal sealed class SupplierComplianceCertificate(
    SupplierComplianceSnapshot snapshot,
    ReportId reportId,
    string verificationHash,
    DateTimeOffset generatedAt) : IDocument
{
    private static readonly Color Ink = Color.FromHex("#1a202c");

    private static readonly Color Muted = Color.FromHex("#64748b");

    private static readonly Color Rule = Color.FromHex("#e2e8f0");

    private static readonly Color Good = Color.FromHex("#15803d");

    private static readonly Color Bad = Color.FromHex("#b91c1c");

    private static readonly Color Warn = Color.FromHex("#b45309");

    public void Compose(IDocumentContainer container) =>
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(text => text.FontSize(10).FontColor(Ink).FontFamily(Fonts.Arial));

            page.Header().Element(Header);
            page.Content().Element(Content);
            page.Footer().Element(Footer);
        });

    private void Header(IContainer container) =>
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text("Supplier Compliance Certificate").FontSize(18).SemiBold();
                    left.Item().PaddingTop(2).Text($"Issued {generatedAt:dd MMMM yyyy 'at' HH:mm} UTC").FontColor(Muted);
                });

                row.ConstantItem(150).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("CERTIFLOW").FontSize(13).SemiBold().FontColor(Muted).LetterSpacing(0.2f);
                    right.Item().AlignRight().PaddingTop(2).Text(StatusLabel()).FontSize(13).SemiBold().FontColor(StatusColour());
                });
            });

            column.Item().PaddingTop(12).LineHorizontal(1).LineColor(Rule);
        });

    private void Content(IContainer container) =>
        container.PaddingVertical(18).Column(column =>
        {
            column.Spacing(18);

            column.Item().Element(Subject);
            column.Item().Element(Summary);
            column.Item().Element(Obligations);
            column.Item().Element(Verification);
        });

    private void Subject(IContainer container) =>
        container.Column(column =>
        {
            column.Item().Text("Supplier").FontSize(11).SemiBold();
            column.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(snapshot.LegalName).SemiBold();

                    if (!string.IsNullOrWhiteSpace(snapshot.TradingName) && snapshot.TradingName != snapshot.LegalName)
                    {
                        left.Item().Text($"trading as {snapshot.TradingName}").FontColor(Muted);
                    }

                    left.Item().PaddingTop(4).Text($"Registration {snapshot.RegistrationNumber} · {snapshot.CountryCode}").FontColor(Muted);
                });

                row.RelativeItem().AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(snapshot.CategoryName).SemiBold();
                    right.Item().AlignRight().PaddingTop(4)
                        .Text($"Compliance profile v{snapshot.ProfileVersion}").FontColor(Muted);
                });
            });
        });

    private void Summary(IContainer container) =>
        container.Background(Color.FromHex("#f8fafc")).Border(1).BorderColor(Rule).Padding(14).Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                left.Item().Text("Position as of").FontColor(Muted).FontSize(9);
                left.Item().PaddingTop(3).Text(snapshot.AsOf.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture)).SemiBold();
            });

            row.RelativeItem().Column(middle =>
            {
                middle.Item().Text("Mandatory requirements met").FontColor(Muted).FontSize(9);
                middle.Item().PaddingTop(3)
                    .Text($"{snapshot.SatisfiedMandatoryCount} of {snapshot.MandatoryCount}").SemiBold();
            });

            row.RelativeItem().AlignRight().Column(right =>
            {
                right.Item().AlignRight().Text("Overall status").FontColor(Muted).FontSize(9);
                right.Item().AlignRight().PaddingTop(3).Text(StatusLabel()).SemiBold().FontColor(StatusColour());
            });
        });

    private void Obligations(IContainer container) =>
        container.Column(column =>
        {
            column.Item().Text("Requirements and evidence").FontSize(11).SemiBold();

            column.Item().PaddingTop(8).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2.4f);
                    columns.RelativeColumn(1.3f);
                    columns.RelativeColumn(2.6f);
                    columns.RelativeColumn(1.5f);
                });

                table.Header(header =>
                {
                    HeaderCell(header.Cell(), "Requirement");
                    HeaderCell(header.Cell(), "Status");
                    HeaderCell(header.Cell(), "Evidence");
                    HeaderCell(header.Cell(), "Valid until");
                });

                foreach (var obligation in snapshot.Obligations)
                {
                    table.Cell().Element(Cell).Column(cell =>
                    {
                        cell.Item().Text(obligation.DocumentType).SemiBold();
                        cell.Item().Text(obligation.IsMandatory ? "Mandatory" : "Optional").FontSize(8).FontColor(Muted);
                    });

                    table.Cell().Element(Cell).Text(obligation.Status).FontColor(ObligationColour(obligation.Status));

                    table.Cell().Element(Cell).Column(cell =>
                    {
                        if (obligation.Evidence is not { } evidence)
                        {
                            // Named explicitly rather than left blank. An empty cell on a compliance
                            // document reads as an oversight; this reads as a finding.
                            cell.Item().Text("No approved evidence").FontColor(Bad);
                            return;
                        }

                        cell.Item().Text(evidence.CertificateNumber).SemiBold();
                        cell.Item().Text(evidence.Issuer).FontSize(8).FontColor(Muted);
                        cell.Item().PaddingTop(2).Text($"Approved by {evidence.Attribution}").FontSize(8).FontColor(Muted);
                    });

                    table.Cell().Element(Cell).Column(cell =>
                    {
                        if (obligation.Evidence is not { } evidence)
                        {
                            cell.Item().Text("—").FontColor(Muted);
                            return;
                        }

                        cell.Item().Text(evidence.ExpiresOn.ToString("dd MMM yyyy", CultureInfo.InvariantCulture));

                        if (obligation.DaysRemaining is { } days)
                        {
                            cell.Item().Text(days < 0 ? $"expired {-days}d ago" : $"{days} days")
                                .FontSize(8)
                                .FontColor(days < 0 ? Bad : days <= 60 ? Warn : Muted);
                        }
                    });
                }
            });
        });

    private void Verification(IContainer container) =>
        container.Column(column =>
        {
            column.Item().Text("Verification").FontSize(11).SemiBold();

            column.Item().PaddingTop(6).Text(text =>
            {
                text.DefaultTextStyle(style => style.FontColor(Muted).FontSize(9));
                text.Span("This certificate is derived from Certiflow's compliance record. The hash below is computed " +
                          "over the facts shown above — not over this file — so a reissued copy of the same position " +
                          "verifies identically, and an altered figure does not. Confirm it at ");
                text.Span($"/api/reports/{reportId.Value}/verify").FontColor(Ink);
                text.Span(".");
            });

            column.Item().PaddingTop(8).Background(Color.FromHex("#f1f5f9")).Padding(8)
                .Text(verificationHash).FontFamily(Fonts.CourierNew).FontSize(9);
        });

    private void Footer(IContainer container) =>
        container.BorderTop(1).BorderColor(Rule).PaddingTop(8).Row(row =>
        {
            row.RelativeItem().Text($"Report {reportId.Value}").FontSize(8).FontColor(Muted);

            row.RelativeItem().AlignRight().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(Muted));
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });

    private static void HeaderCell(IContainer container, string label) =>
        container.BorderBottom(1).BorderColor(Rule).PaddingBottom(4)
            .Text(label).FontSize(8).SemiBold().FontColor(Muted);

    private static IContainer Cell(IContainer container) =>
        container.BorderBottom(1).BorderColor(Rule).PaddingVertical(8).PaddingRight(6);

    private string StatusLabel() => snapshot.OverallStatus switch
    {
        "Compliant" => "COMPLIANT",
        "NonCompliant" => "NON-COMPLIANT",
        "ExpiringSoon" => "EXPIRING SOON",
        _ => snapshot.OverallStatus.ToUpperInvariant(),
    };

    private Color StatusColour() => snapshot.OverallStatus switch
    {
        "Compliant" => Good,
        "NonCompliant" => Bad,
        _ => Warn,
    };

    private static Color ObligationColour(string status) => status switch
    {
        "Satisfied" => Good,
        "Breached" or "Missing" or "Expired" => Bad,
        _ => Warn,
    };
}
