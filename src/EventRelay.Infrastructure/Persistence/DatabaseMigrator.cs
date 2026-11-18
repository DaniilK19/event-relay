using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EventRelay.Infrastructure.Persistence;

public static class DatabaseMigrator
{
    /// <summary>
    /// Applies pending EF Core migrations. On a brand-new database EF probes the migrations
    /// history table before creating it and logs that expected failure as an error; this
    /// context lowers just that event to Debug. Real migration failures still throw.
    /// EF Core takes a database lock while migrating, so concurrent callers are safe.
    /// </summary>
    public static async Task MigrateAsync(string connectionString, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<EventRelayDbContext>();
        EventRelayDbContext.Configure(options, connectionString)
            .UseLoggerFactory(loggerFactory)
            .ConfigureWarnings(warnings => warnings.Log((RelationalEventId.CommandError, LogLevel.Debug)));

        await using var db = new EventRelayDbContext(options.Options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
