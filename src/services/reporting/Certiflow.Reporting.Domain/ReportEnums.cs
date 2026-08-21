namespace Certiflow.Reporting.Domain;

public enum ReportType
{
    /// <summary>A single supplier's compliance position, with its evidence (FR-6.1).</summary>
    SupplierComplianceCertificate = 1,
}

public enum ReportStatus
{
    /// <summary>Accepted and queued. Generation has not started (FR-6.4).</summary>
    Requested = 1,

    Generating = 2,

    Completed = 3,

    Failed = 4,
}
