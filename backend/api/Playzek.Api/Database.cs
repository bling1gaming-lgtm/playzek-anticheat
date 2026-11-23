using Microsoft.EntityFrameworkCore;
using Playzek.Data;

namespace Playzek.Api;

public static class Database
{
    /// <summary>
    /// Playzek:Database:Provider = "Postgres" (producție, implicit) sau "Sqlite" (doar teste / dezvoltare locală).
    /// Conexiunea vine din ConnectionStrings:Playzek (variabilă de mediu ConnectionStrings__Playzek).
    /// </summary>
    public static IServiceCollection AddPlayzekDatabase(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Playzek:Database:Provider"] ?? "Postgres";
        var cs = config.GetConnectionString("Playzek");
        services.AddDbContext<PlayzekDbContext>(o =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                o.UseSqlite(cs ?? "Data Source=playzek-dev.db");
            else
                o.UseNpgsql(cs ?? throw new InvalidOperationException(
                    "ConnectionStrings:Playzek lipsește. Setează variabila de mediu ConnectionStrings__Playzek."));
        });
        return services;
    }

    public static async Task InitializeDatabaseAsync(this IServiceProvider services, IConfiguration config)
    {
        if (!config.GetValue("Playzek:Database:MigrateOnStartup", false)) return;
        await using var scope = services.CreateAsyncScope();
        await MigrateAsync(scope.ServiceProvider.GetRequiredService<PlayzekDbContext>());
    }

    public static async Task MigrateAsync(PlayzekDbContext db)
    {
        // Migrațiile sunt generate pentru PostgreSQL. Pe SQLite (teste) se creează schema direct din model.
        if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
    }
}
