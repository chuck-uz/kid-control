using KidControl.Backend.Entities;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Persistence;

/// <summary>
/// EF Core context for the fleet backend. Maps the §4 data model to snake_case PostgreSQL
/// tables. The reserved single-family <see cref="Tenant"/> is seeded here (deterministic
/// <c>HasData</c>); the operator <see cref="Admin"/> is seeded at runtime by
/// <see cref="FleetSeed"/> because its Telegram chat id comes from configuration.
/// </summary>
public sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DevicePolicy> DevicePolicies => Set<DevicePolicy>();
    public DbSet<DeviceDesired> DeviceDesired => Set<DeviceDesired>();
    public DbSet<DeviceStatus> DeviceStatuses => Set<DeviceStatus>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<EnrollCode> EnrollCodes => Set<EnrollCode>();
    public DbSet<Audit> Audits => Set<Audit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenant");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            // The single reserved family (decision #1). Admin row is seeded at runtime.
            e.HasData(new Tenant { Id = Tenant.DefaultId, Name = "Семья" });
        });

        b.Entity<Admin>(e =>
        {
            e.ToTable("admin");
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).HasMaxLength(120);
            e.HasOne(x => x.Tenant).WithMany(t => t.Admins)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TenantId, x.TelegramChatId }).IsUnique();
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("device");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.GroupLabel).HasMaxLength(120);
            e.Property(x => x.AgentVersion).HasMaxLength(60);
            e.Property(x => x.OsInfo).HasMaxLength(200);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasOne(x => x.Tenant).WithMany(t => t.Devices)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            // Bearer-token lookup on every agent call: unique over the non-empty hashes.
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.LastSeenAt);
        });

        b.Entity<DevicePolicy>(e =>
        {
            e.ToTable("device_policy");
            e.HasKey(x => x.DeviceId); // one-to-one with device
            e.Property(x => x.TargetVersion).HasMaxLength(60).IsRequired();
            e.HasOne(x => x.Device).WithOne(d => d.Policy)
                .HasForeignKey<DevicePolicy>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceDesired>(e =>
        {
            e.ToTable("device_desired");
            e.HasKey(x => x.DeviceId);
            e.HasOne(x => x.Device).WithOne(d => d.Desired)
                .HasForeignKey<DeviceDesired>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceStatus>(e =>
        {
            e.ToTable("device_status");
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.HasOne(x => x.Device).WithOne(d => d.Status)
                .HasForeignKey<DeviceStatus>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Command>(e =>
        {
            e.ToTable("command");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(40).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.Result).HasMaxLength(2000);
            e.HasOne(x => x.Device).WithMany(d => d.Commands)
                .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            // Pending-command queue per device: undelivered/unacked, newest first.
            e.HasIndex(x => new { x.DeviceId, x.AckedAt });
        });

        b.Entity<EnrollCode>(e =>
        {
            e.ToTable("enroll_code");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasOne(x => x.Tenant).WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<Audit>(e =>
        {
            e.ToTable("audit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(120).IsRequired();
            e.Property(x => x.Action).HasMaxLength(80).IsRequired();
            e.Property(x => x.DetailJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.At });
        });
    }
}
