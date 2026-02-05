using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.EntityFrameworkCore;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorCore.Db;

public class Db : DbContext
{
    public DbSet<UserSetting> UserSettings { get; set; }

    private static Db? Instance { get; set; }

    /// <summary>
    /// Gets a shared DbContext instance for synchronous operations like migrations.
    /// WARNING: Do NOT use this for concurrent async operations - use CreateContext() instead.
    /// </summary>
    public static Db GetInstance()
    {
        return Instance ??= new Db();
    }

    /// <summary>
    /// Creates a new DbContext instance for async operations.
    /// Each concurrent operation should use its own context to avoid threading issues.
    /// The caller is responsible for disposing the context.
    /// </summary>
    public static Db CreateContext()
    {
        return new Db();
    }

    public static void Disconnect()
    {
        Instance?.Dispose();
        Instance = null;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

        var configData = Configs.IsLoaded() ? Configs.GetConfigData() : new ConfigData();
        var databaseConnectionString = configData.DatabaseConnectionString;

        // MySQL only
        Utils.SetupMySql(databaseConnectionString, optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        UserSetting.Configure(configurationBuilder);
        configurationBuilder
            .Properties<CsItem?>()
            .HaveConversion<CsItemConverter>();
    }
}
