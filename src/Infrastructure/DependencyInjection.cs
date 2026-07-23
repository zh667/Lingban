using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Infrastructure.Calendar;
using Lingban.Infrastructure.Data;
using Lingban.Infrastructure.Data.Interceptors;
using Lingban.Infrastructure.Diagnostics;
using Lingban.Infrastructure.Identity;
using Lingban.Infrastructure.Tenancy;
using Lingban.Infrastructure.Verification;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static void AddInfrastructureServices(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(Services.Database);
        Guard.Against.Null(connectionString, message: $"Connection string '{Services.Database}' not found.");

        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddScoped<ISaveChangesInterceptor, TenantInterceptor>();
        builder.Services.AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>();
        builder.Services.AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();

        builder.Services.AddScoped<IGenealogySerializedExecutor, GenealogySerializedExecutor>();
        builder.Services.AddScoped<IQueryLog, QueryLog>();
        builder.Services.AddScoped<SqlCaptureInterceptor>();
        builder.Services.AddScoped<IFactoryCalendarProvider, FactoryCalendarProvider>();
        builder.Services.AddScoped<IVerificationQueryService, VerificationQueryService>();
        builder.Services.AddScoped<IFactVerifier, FactVerifier>();
        builder.Services.AddScoped<IVerificationRule, TodayWorkOrdersVerificationRule>();
        builder.Services.AddScoped<IVerificationRule, DelayedOrdersVerificationRule>();
        builder.Services.AddScoped<IVerificationRule, DefectSummaryVerificationRule>();
        builder.Services.AddScoped<IVerificationRule, OeeVerificationRule>();

        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<SqlCaptureInterceptor>());
            options.UseNpgsql(connectionString);
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        builder.EnrichNpgsqlDbContext<ApplicationDbContext>();

        builder.Services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());

        builder.Services.AddScoped<ApplicationDbContextInitialiser>();

        builder.Services.AddAuthentication()
            .AddBearerToken(IdentityConstants.BearerScheme);

        builder.Services.AddAuthorizationBuilder();

        builder.Services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddApiEndpoints();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddTransient<IIdentityService, IdentityService>();
    }
}
