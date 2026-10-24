using Microsoft.Extensions.DependencyInjection;
using MiniMcp.Configuration;

namespace MiniMcp.Services;

internal static class ToolExecutionRegistration
{
    public static IServiceCollection AddToolExecution(this IServiceCollection services, Guid runId, RuntimeConfiguration configuration) =>
        // A factory makes the provider own disposal; every request scope resolves the same instance.
        services.AddSingleton(_ => new ToolExecutionService(runId, configuration));
}
