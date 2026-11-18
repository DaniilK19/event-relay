using EventRelay.Core;
using EventRelay.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventRelay.Infrastructure.Persistence;

// Column names come from the snake_case naming convention (EFCore.NamingConventions).
// Raw SQL elsewhere in Infrastructure relies on these names.

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Type).HasMaxLength(EventTypeRules.MaxLength);
        builder.Property(e => e.Source).HasMaxLength(200);
        builder.Property(e => e.Attributes).IsJsonbDictionary();
        builder.Property(e => e.Data).HasColumnType("jsonb");
        builder.HasIndex(e => e.ReceivedAt);
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(Subscription.MaxNameLength);
        builder.Property(s => s.EventTypes).HasColumnType("text[]");
        builder.Property(s => s.Filter).IsJsonbDictionary();
        builder.Property(s => s.WebhookUrl).HasMaxLength(Subscription.MaxUrlLength);
        builder.Property(s => s.Secret).HasMaxLength(Subscription.MaxSecretLength);

        // Fan-out looks subscriptions up with "event_types && candidate_patterns".
        builder.HasIndex(s => s.EventTypes).HasMethod("gin");
    }
}

internal sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        builder.ToTable("deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.LastError).HasMaxLength(Delivery.MaxErrorLength);

        // One delivery per (event, subscription): the database-level guarantee behind idempotent fan-out.
        builder.HasIndex(d => new { d.EventId, d.SubscriptionId }).IsUnique();

        // The delivery worker only scans pending rows that are due.
        builder.HasIndex(d => d.NextAttemptAt)
            .HasDatabaseName("ix_deliveries_due")
            .HasFilter("status = 'Pending'");

        builder.HasIndex(d => new { d.SubscriptionId, d.CreatedAt });

        builder.HasOne<Event>().WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Subscription>().WithMany().HasForeignKey(d => d.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).UseIdentityAlwaysColumn();
        builder.Property(m => m.Type).HasMaxLength(EventTypeRules.MaxLength);
        builder.Property(m => m.Payload).HasColumnType("jsonb");
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);

        // The publisher only scans rows that have not been sent yet.
        builder.HasIndex(m => m.Id)
            .HasDatabaseName("ix_outbox_messages_unprocessed")
            .HasFilter("processed_at IS NULL");
    }
}

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");
        builder.HasKey(m => new { m.Consumer, m.MessageId });
        builder.Property(m => m.Consumer).HasMaxLength(100);
        builder.Property(m => m.MessageId).HasMaxLength(128);
    }
}
