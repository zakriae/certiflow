using Certiflow.Intake.Application.Abstractions;
using Certiflow.Intake.Infrastructure.Persistence;
using Certiflow.Intake.Infrastructure.Storage;
using Certiflow.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Certiflow.Intake.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntakeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<IntakeDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("IntakeDatabase"),
                sql =>
                {
                    // Migration history lives in this context's own schema, so eight contexts
                    // sharing one database do not fight over one history table (SRS §13.1).
                    sql.MigrationsHistoryTable("__migrations", IntakeDbContext.Schema);

                    // Transient SQL faults are normal in the cloud, not exceptional. Without this a
                    // routine Azure SQL failover surfaces as a failed upload.
                    sql.EnableRetryOnFailure();
                }));

        services.Configure<BlobStorageOptions>(configuration.GetSection(BlobStorageOptions.SectionName));

        services.AddSingleton<IDocumentBlobStore, BlobDocumentStore>();
        services.AddSingleton<IDocumentInspector, DocumentInspector>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IUnitOfWork, OutboxUnitOfWork>();

        return services;
    }
}
