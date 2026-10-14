using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ThrottlingMiddleware;

public static class ThrottlingExtensions
{
    public static IServiceCollection AddThrottling(this IServiceCollection services, Action<ThrottlingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<ThrottlingOptions>().Configure(configure);
        Register(services);
        return services;
    }

    public static IServiceCollection AddThrottling(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ThrottlingOptions>().Bind(configuration);
        Register(services);
        return services;
    }

    public static IApplicationBuilder UseThrottling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ThrottlingMiddleware>();
    }

    private static void Register(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<ThrottlingOptions>, ThrottlingOptionsValidator>();
        services.AddOptions<ThrottlingOptions>().ValidateOnStart();
        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<ThrottlingOptions>>().Value;
            var created = RuleSet.Create(options);
            if (created.Rules is null)
            {
                throw new OptionsValidationException(
                    Options.DefaultName,
                    typeof(ThrottlingOptions),
                    created.Errors);
            }

            return created.Rules;
        });
        services.AddSingleton(static provider => new GateRegistry(provider.GetRequiredService<RuleSet>()));
    }
}
