using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TwilioCaller.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TwilioConnection> Connections => Set<TwilioConnection>();
    public DbSet<DeviceRegistration> Devices => Set<DeviceRegistration>();
    public DbSet<InboundEvent> InboundEvents => Set<InboundEvent>();

    /// <summary>
    /// SQLite has no native DateTimeOffset, and EF cannot translate comparisons or
    /// ordering against one. Storing Unix milliseconds keeps both working and keeps
    /// the ordering identical to absolute time regardless of the original offset.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, long> UnixMilliseconds = new(
        value => value.ToUnixTimeMilliseconds(),
        value => DateTimeOffset.FromUnixTimeMilliseconds(value));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableUnixMilliseconds = new(
        value => value == null ? null : value.Value.ToUnixTimeMilliseconds(),
        value => value == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value));

    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(UnixMilliseconds);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(NullableUnixMilliseconds);
                }
            }
        }

        b.Entity<TwilioConnection>()
            .HasIndex(c => c.AccountSid);

        b.Entity<DeviceRegistration>()
            .HasIndex(d => new { d.ConnectionId, d.Identity })
            .IsUnique();

        b.Entity<DeviceRegistration>()
            .HasOne(d => d.Connection)
            .WithMany(c => c.Devices)
            .HasForeignKey(d => d.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<InboundEvent>()
            .HasIndex(e => new { e.ConnectionId, e.ReceivedAt });

        b.Entity<InboundEvent>()
            .HasIndex(e => e.TwilioSid)
            .IsUnique();
    }
}
