using Certiflow.Reporting.Application.Abstractions;
using Certiflow.Reporting.Domain;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Certiflow.Reporting.Infrastructure.Rendering;

public sealed class QuestPdfReportRenderer : IReportRenderer
{
    static QuestPdfReportRenderer()
    {
        // MIT-licensed for our size; the SRS chose QuestPDF partly to keep the licence cost at zero.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Render(
        SupplierComplianceSnapshot snapshot,
        ReportId reportId,
        string verificationHash,
        DateTimeOffset generatedAt) =>
        new SupplierComplianceCertificate(snapshot, reportId, verificationHash, generatedAt).GeneratePdf();
}
