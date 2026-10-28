using AFP.Application.Abstractions;
using AFP.Application.Services;
using AFP.Application.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IActionAuditor, ActionAuditor>();
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAdminBootstrapService, AdminBootstrapService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IAircraftService, AircraftService>();
        services.AddScoped<ITelemetryService, TelemetryService>();
        services.AddScoped<IImportDeletionService, ImportDeletionService>();
        services.AddScoped<IAnomalyWorkOrderService, AnomalyWorkOrderService>();
        services.AddScoped<ITelemetryImportProcessor, TelemetryImportProcessor>();
        services.AddScoped<IAnomalyService, AnomalyService>();
        services.AddScoped<IFailurePredictionStatusProvider, FailurePredictionStatusProvider>();
        services.AddScoped<IPredictionService, PredictionService>();
        services.AddScoped<IWorkOrderService, WorkOrderService>();
        services.AddScoped<IMaintenanceLogService, MaintenanceLogService>();
        services.AddScoped<IPipelineService, PipelineService>();
        services.AddScoped<IModelService, ModelService>();
        services.AddScoped<IDevelopmentLabService, DevelopmentLabService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IFleetHealthEvaluator, FleetHealthEvaluator>();
        services.AddScoped<IWorkOrderDiagnosisService, WorkOrderDiagnosisService>();
        services.AddScoped<IAnomalyProcessingService, AnomalyProcessingService>();
        services.AddScoped<IForecastService, ForecastService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IReportService, ReportService>();
        return services;
    }
}
