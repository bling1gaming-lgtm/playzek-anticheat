using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Playzek.Authentication;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlayzekAuthentication(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<SecurityPolicyOptions>(config.GetSection(SecurityPolicyOptions.Section));
        services.Configure<SessionPolicyOptions>(config.GetSection(SessionPolicyOptions.Section));
        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<TokenService>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<BanLookup>();
        services.AddScoped<PlayerAuthService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<AntiCheatSessionService>();
        services.AddScoped<GameServerService>();
        return services;
    }
}
