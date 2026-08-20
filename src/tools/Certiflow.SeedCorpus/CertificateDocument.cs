using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Certiflow.SeedCorpus;

/// <summary>
/// Renders one certificate to PDF.
/// <para>
/// <b>The governing constraint is that the extraction pipeline must be able to read this back.</b>
/// Every field value is written on a single line and never wrapped, because PdfPig reconstructs a
/// line break as whitespace: a certificate number split across two lines cannot be located
/// verbatim, grounding fails, confidence drops to zero, and a perfectly good document is sent to a
/// human reviewer for a reason that has nothing to do with the document.
/// </para>
/// <para>
/// The layouts differ so the pipeline is not fitted to one template. What stays constant is the
/// vocabulary — "Certificate No.", "Valid until" — because that is what a real certification body
/// does too, and the model is given the labels to anchor on.
/// </para>
/// </summary>
public sealed class CertificateDocument(SeedCertificate certificate) : IDocument
{
    private bool IsFrench => certificate.Language == CorpusLanguage.French;

    private CultureInfo Culture => IsFrench ? new CultureInfo("fr-FR") : new CultureInfo("en-GB");

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"{certificate.DocumentType} — {certificate.CertificateNumber}",
        Author = certificate.IssuerName,
        Subject = certificate.Scope,
    };

    public void Compose(IDocumentContainer container) => container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(style => style.FontSize(10).LineHeight(1.35f));

        page.Content().Element(content =>
        {
            switch (certificate.Layout)
            {
                case CertificateLayout.Tabular:
                    ComposeTabular(content);
                    break;
                case CertificateLayout.Compact:
                    ComposeCompact(content);
                    break;
                default:
                    ComposeClassic(content);
                    break;
            }
        });

        page.Footer().PaddingTop(10).BorderTop(0.5f).PaddingTop(6).Column(column =>
        {
            column.Item().Text(certificate.IssuerName).FontSize(8).FontColor(Colors.Grey.Darken1);
            column.Item().Text(Label("This certificate remains the property of the issuing body.",
                                    "Ce certificat demeure la propriété de l'organisme émetteur."))
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    });

    // ── Layout 1: formal and centred ─────────────────────────────────────────────────────────

    private void ComposeClassic(IContainer container) => container.Column(column =>
    {
        column.Spacing(14);

        column.Item().AlignCenter().Text(certificate.IssuerName.ToUpperInvariant())
            .FontSize(13).Bold().LetterSpacing(0.12f);

        column.Item().AlignCenter().Text(Label("CERTIFICATE OF REGISTRATION", "CERTIFICAT D'ENREGISTREMENT"))
            .FontSize(19).Bold();

        column.Item().PaddingVertical(4).LineHorizontal(1);

        column.Item().AlignCenter().Text(Label("This is to certify that the management system of",
                                               "Il est certifié que le système de management de"))
            .FontSize(10).Italic();

        // Holder name gets its own line at a size that cannot wrap - the single most important
        // value on the page for the entity-match check.
        column.Item().AlignCenter().Text(certificate.HolderName).FontSize(16).Bold();
        column.Item().AlignCenter().Text(certificate.HolderAddress).FontSize(9);

        if (certificate.Standard is not null)
        {
            column.Item().PaddingTop(6).AlignCenter().Text(Label(
                $"has been assessed and found to conform to {certificate.Standard}",
                $"a été évalué et jugé conforme à la norme {certificate.Standard}")).FontSize(10);
        }

        column.Item().PaddingTop(8).Element(e => KeyValueBlock(e,
        [
            (Label("Certificate No.", "N° de certificat"), certificate.CertificateNumber),
            (Label("Date of issue", "Date de délivrance"), FormatDate(certificate.IssuedOn)),
            (Label("Valid until", "Valable jusqu'au"), FormatDate(certificate.ExpiresOn)),
        ]));

        column.Item().PaddingTop(6).Text(text =>
        {
            text.Span(Label("Scope of certification: ", "Domaine d'application : ")).Bold().FontSize(9);
            text.Span(certificate.Scope).FontSize(9);
        });
    });

    // ── Layout 2: label/value table ──────────────────────────────────────────────────────────

    private void ComposeTabular(IContainer container) => container.Column(column =>
    {
        column.Spacing(12);

        column.Item().Row(row =>
        {
            row.RelativeItem().Column(inner =>
            {
                inner.Item().Text(certificate.IssuerName).FontSize(12).Bold();
                inner.Item().Text(Label("Certification Services", "Services de certification"))
                    .FontSize(8).FontColor(Colors.Grey.Darken1);
            });

            row.ConstantItem(160).AlignRight().Column(inner =>
            {
                inner.Item().AlignRight().Text(Label("Certificate No.", "N° de certificat"))
                    .FontSize(8).FontColor(Colors.Grey.Darken1);
                inner.Item().AlignRight().Text(certificate.CertificateNumber).FontSize(11).Bold();
            });
        });

        column.Item().LineHorizontal(1.5f);

        column.Item().PaddingTop(4).Text(TitleFor(certificate.DocumentType)).FontSize(16).Bold();

        column.Item().Element(e => KeyValueBlock(e,
        [
            (Label("Issued to", "Délivré à"), certificate.HolderName),
            (Label("Registered address", "Adresse du siège"), certificate.HolderAddress),
            .. certificate.Standard is null
                ? Array.Empty<(string, string)>()
                : [(Label("Standard", "Norme"), certificate.Standard)],
            (Label("Date of issue", "Date de délivrance"), FormatDate(certificate.IssuedOn)),
            (Label("Valid until", "Valable jusqu'au"), FormatDate(certificate.ExpiresOn)),
        ]));

        column.Item().PaddingTop(8).Column(inner =>
        {
            inner.Item().Text(Label("Scope", "Domaine d'application")).Bold().FontSize(9);
            inner.Item().Text(certificate.Scope).FontSize(9);
        });
    });

    // ── Layout 3: dense ──────────────────────────────────────────────────────────────────────

    private void ComposeCompact(IContainer container) => container.Column(column =>
    {
        column.Spacing(10);

        column.Item().Text($"{certificate.IssuerName} — {TitleFor(certificate.DocumentType)}")
            .FontSize(12).Bold();

        column.Item().LineHorizontal(0.75f);

        column.Item().Element(e => KeyValueBlock(e,
        [
            (Label("Certificate No.", "N° de certificat"), certificate.CertificateNumber),
            (Label("Holder", "Titulaire"), certificate.HolderName),
            (Label("Address", "Adresse"), certificate.HolderAddress),
            .. certificate.Standard is null
                ? Array.Empty<(string, string)>()
                : [(Label("Standard", "Norme"), certificate.Standard)],
            (Label("Date of issue", "Date de délivrance"), FormatDate(certificate.IssuedOn)),
            (Label("Valid until", "Valable jusqu'au"), FormatDate(certificate.ExpiresOn)),
        ]));

        column.Item().PaddingTop(6).Text(text =>
        {
            text.Span(Label("Scope: ", "Domaine : ")).Bold().FontSize(9);
            text.Span(certificate.Scope).FontSize(9);
        });

        column.Item().PaddingTop(10).Text(Label(
            "Continued validity is subject to successful surveillance audits.",
            "Le maintien de la validité est soumis à des audits de surveillance favorables."))
            .FontSize(8).Italic();
    });

    // ── Shared pieces ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Label/value rows in a fixed-width grid. The value column is wide enough that no seeded
    /// value wraps, which is what keeps each one locatable as a single verbatim string.
    /// </summary>
    private static void KeyValueBlock(IContainer container, IReadOnlyList<(string Key, string Value)> rows) =>
        container.Column(column =>
        {
            column.Spacing(3);

            foreach (var (key, value) in rows)
            {
                column.Item().Row(row =>
                {
                    row.ConstantItem(140).Text(key).FontSize(9).FontColor(Colors.Grey.Darken2);
                    row.RelativeItem().Text(value).FontSize(10);
                });
            }
        });

    private string Label(string english, string french) => IsFrench ? french : english;

    private string FormatDate(DateOnly date) => date.ToString("d MMMM yyyy", Culture);

    private string TitleFor(string documentType) => documentType switch
    {
        "Public Liability Insurance" => Label("Certificate of Insurance", "Attestation d'assurance"),
        "Trade Licence" => Label("Trade Licence", "Licence commerciale"),
        "Safety Training Record" => Label("Training Record", "Attestation de formation"),
        "Food Hygiene Certificate" => Label("Food Hygiene Certificate", "Certificat d'hygiène alimentaire"),
        _ => Label("Certificate of Registration", "Certificat d'enregistrement"),
    };
}
