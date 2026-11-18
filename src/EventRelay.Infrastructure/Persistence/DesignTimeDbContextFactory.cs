using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventRelay.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef migrations add</c>. Building the model needs no live database,
/// so the connection string is a placeholder.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EventRelayDbContext>
{
    public EventRelayDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<EventRelayDbContext>();
        EventRelayDbContext.Configure(builder, "Host=localhost;Database=eventrelay_design_time");
        return new EventRelayDbContext(builder.Options);
    }
}
