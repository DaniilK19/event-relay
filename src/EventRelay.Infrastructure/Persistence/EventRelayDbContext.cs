using EventRelay.Core;
using EventRelay.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EventRelay.Infrastructure.Persistence;

public sealed class EventRelayDbContext(DbContextOptions<EventRelayDbContext> options) : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    /// <summary>Single place for provider settings, shared by DI and the design-time factory.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EventRelayDbContext).Assembly);
}
