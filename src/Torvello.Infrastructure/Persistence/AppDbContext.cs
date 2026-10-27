using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Torvello.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SyncEvent> SyncEvents => Set<SyncEvent>();
    public DbSet<SyncedOrder> SyncedOrders => Set<SyncedOrder>();
    public DbSet<InventoryIngredient> InventoryIngredients => Set<InventoryIngredient>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(60).IsRequired();
        });

        builder.Entity<RefreshToken>(token =>
        {
            token.ToTable("RefreshTokens");
            token.HasKey(t => t.Id);
            token.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.FamilyId);
            token.Property(t => t.ReplacedByHash).HasMaxLength(64);
            token.Property(t => t.CreatedByIp).HasMaxLength(45);
            token.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("AuditEvents");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Action).HasMaxLength(60).IsRequired();
            audit.Property(a => a.Details).HasMaxLength(2000);
            audit.Property(a => a.IpAddress).HasMaxLength(45);
            audit.HasIndex(a => a.OccurredAt);
            audit.HasIndex(a => a.UserId);
        });

        builder.Entity<Device>(device =>
        {
            device.ToTable("Devices");
            device.HasKey(d => d.Id);
            device.Property(d => d.Name).HasMaxLength(60).IsRequired();
            device.Property(d => d.KeyHash).HasMaxLength(64).IsRequired();
        });

        builder.Entity<SyncEvent>(sync =>
        {
            sync.ToTable("SyncEvents");
            sync.HasKey(e => e.EventId);
            sync.Property(e => e.EventType).HasMaxLength(60).IsRequired();
            sync.Property(e => e.Payload).IsRequired();
            sync.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            sync.HasIndex(e => new { e.DeviceId, e.ReceivedAt });
            sync.HasOne<Device>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SyncedOrder>(order =>
        {
            order.ToTable("SyncedOrders");
            order.HasKey(o => o.Id);
            order.Property(o => o.ReceiptNumber).HasMaxLength(40).IsRequired();
            order.Property(o => o.OrderType).HasMaxLength(20).IsRequired();
            order.Property(o => o.EmployeeName).HasMaxLength(60);
            order.HasIndex(o => o.BusinessDate);
            order.HasIndex(o => new { o.DeviceId, o.ReceiptNumber }).IsUnique();
            order.HasOne<Device>().WithMany().HasForeignKey(o => o.DeviceId).OnDelete(DeleteBehavior.Restrict);
            order.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
            order.HasMany(o => o.Payments).WithOne().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SyncedOrderLine>(line =>
        {
            line.ToTable("SyncedOrderLines");
            line.Property(l => l.Name).HasMaxLength(120).IsRequired();
        });

        builder.Entity<SyncedPayment>(payment =>
        {
            payment.ToTable("SyncedPayments");
            payment.Property(p => p.Method).HasMaxLength(20).IsRequired();
        });

        builder.Entity<InventoryIngredient>(ingredient =>
        {
            ingredient.ToTable("InventoryIngredients");
            ingredient.HasKey(i => i.Id);
            ingredient.Property(i => i.Name).HasMaxLength(60).IsRequired();
            ingredient.Property(i => i.Unit).HasMaxLength(8).IsRequired();
        });

        builder.Entity<InventoryMovement>(movement =>
        {
            movement.ToTable("InventoryMovements");
            movement.HasKey(m => m.Id);
            movement.Property(m => m.Type).HasMaxLength(20).IsRequired();
            movement.Property(m => m.Note).HasMaxLength(200);
            movement.Property(m => m.EmployeeName).HasMaxLength(60);
            movement.HasIndex(m => new { m.IngredientId, m.OccurredAtMs });
            // A sale deducts each ingredient once, however often the event is uploaded.
            movement.HasIndex(m => new { m.SourceEventId, m.IngredientId }).IsUnique();
        });
    }
}
