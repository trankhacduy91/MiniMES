using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Common.Behaviors;

namespace MiniMes.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        // MediatR 12.x (Apache 2.0); v13+ needs a commercial/community license key, see README.
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            // Behaviors run in registration order: validation first, before any handler touches the DB.
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly);
        services.AddScoped<Material.IMaterialGate, Material.MaterialGate>();   // M6: asked by the scan (M3)
        services.AddScoped<Execution.ScanExecutor>();                           // M3b: the scan core, shared by Scan and Pack
        services.AddScoped<Packing.PackingLabels>();                            // M3b: carton/pallet labels and their print log
        return services;
    }
}
