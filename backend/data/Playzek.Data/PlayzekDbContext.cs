using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Playzek.Data;

/// <summary>
/// Schema PLAYZEK ANTI-CHEAT. Producția folosește PostgreSQL (migrații în Migrations/).
/// Modelul evită tipurile specifice unui furnizor, ca testele locale să poată rula și pe SQLite.
/// </summary>
public class PlayzekDbContext(DbContextOptions<PlayzekDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<RegisteredDevice> RegisteredDevices => Set<RegisteredDevice>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<GameServer> GameServers => Set<GameServer>();
    public DbSet<AntiCheatSession> AntiCheatSessions => Set<AntiCheatSession>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();
    public DbSet<ClientVersion> ClientVersions => Set<ClientVersion>();
    public DbSet<IntegrityReport> IntegrityReports => Set<IntegrityReport>();
    public DbSet<Ban> Bans => Set<Ban>();
    public DbSet<AdministratorRole> AdministratorRoles => Set<AdministratorRole>();
    public DbSet<AdministratorAccount> AdministratorAccounts => Set<AdministratorAccount>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
        builder.Properties<string>().HaveMaxLength(256);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(32);
            e.Property(x => x.NormalizedUsername).HasMaxLength(32);
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.HasOne(x => x.Profile).WithOne(p => p.User).HasForeignKey<PlayerProfile>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlayerProfile>(e =>
        {
            e.ToTable("player_profiles");
            e.HasKey(x => x.UserId);
            e.Property(x => x.DisplayName).HasMaxLength(32);
            e.Property(x => x.SteamId64).HasMaxLength(17);
            e.HasIndex(x => x.SteamId64).IsUnique();
        });

        b.Entity<RegisteredDevice>(e =>
        {
            e.ToTable("registered_devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.FingerprintHash).HasMaxLength(64);
            e.Property(x => x.DeviceName).HasMaxLength(64);
            e.Property(x => x.OsVersion).HasMaxLength(64);
            e.Property(x => x.ClientVersion).HasMaxLength(32);
            e.HasIndex(x => new { x.UserId, x.FingerprintHash }).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Devices).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.RevokedReason).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GameServer>(e =>
        {
            e.ToTable("game_servers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Address).HasMaxLength(128);
            e.Property(x => x.ApiKeyPrefix).HasMaxLength(16);
            e.Property(x => x.ApiKeyHash).HasMaxLength(64);
            e.HasIndex(x => x.ApiKeyHash).IsUnique();
            e.HasIndex(x => x.ApiKeyPrefix).IsUnique();
            e.Property(x => x.PluginVersion).HasMaxLength(32);
        });

        b.Entity<AntiCheatSession>(e =>
        {
            e.ToTable("anti_cheat_sessions", t =>
                t.HasCheckConstraint("ck_anti_cheat_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\""));
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Status });
            e.Property(x => x.ClientVersion).HasMaxLength(32);
            e.Property(x => x.EndReason).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GameSession>(e =>
        {
            e.ToTable("game_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.SteamId64).HasMaxLength(17);
            e.Property(x => x.DecisionCode).HasMaxLength(48);
            e.HasIndex(x => new { x.GameServerId, x.JoinedAt });
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.GameServer).WithMany().HasForeignKey(x => x.GameServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.AntiCheatSession).WithMany().HasForeignKey(x => x.AntiCheatSessionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ClientVersion>(e =>
        {
            e.ToTable("client_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(32);
            e.Property(x => x.Channel).HasMaxLength(16);
            e.HasIndex(x => new { x.Version, x.Channel }).IsUnique();
            e.Property(x => x.Notes).HasMaxLength(1024);
        });

        b.Entity<IntegrityReport>(e =>
        {
            e.ToTable("integrity_reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.WindowsVersion).HasMaxLength(64);
            e.Property(x => x.ClientVersion).HasMaxLength(32);
            e.Property(x => x.Category).HasMaxLength(64);
            e.Property(x => x.EvidenceReference).HasMaxLength(256);
            e.Property(x => x.Summary).HasMaxLength(1024);
            e.HasIndex(x => new { x.UserId, x.ReceivedAt });
            e.HasIndex(x => x.AntiCheatSessionId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AntiCheatSession).WithMany().HasForeignKey(x => x.AntiCheatSessionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Ban>(e =>
        {
            e.ToTable("bans", t => t.HasCheckConstraint("ck_bans_temporary_expiry",
                "\"Type\" <> 'Temporary' OR \"ExpiresAt\" IS NOT NULL"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(512);
            e.HasIndex(x => new { x.UserId, x.LiftedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AdministratorAccount>().WithMany().HasForeignKey(x => x.CreatedByAdministratorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AdministratorRole>(e =>
        {
            e.ToTable("administrator_roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(32);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Permissions).HasMaxLength(2048);
        });

        b.Entity<AdministratorAccount>(e =>
        {
            e.ToTable("administrator_accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(32);
            e.Property(x => x.NormalizedUsername).HasMaxLength(32);
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.MfaSecret).HasMaxLength(256);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ActorId).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.TargetId).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.Details).HasMaxLength(4096);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.ActorType, x.ActorId });
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(4096);
        });
    }
}

/// <summary>Folosit doar de „dotnet ef” ca să genereze migrațiile PostgreSQL. Nu se conectează la nimic.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlayzekDbContext>
{
    public PlayzekDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlayzekDbContext>()
            .UseNpgsql("Host=localhost;Database=playzek_design_time")
            .Options;
        return new PlayzekDbContext(options);
    }
}
