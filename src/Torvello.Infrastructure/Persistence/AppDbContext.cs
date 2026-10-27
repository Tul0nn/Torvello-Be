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
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyAcknowledgement> PolicyAcknowledgements => Set<PolicyAcknowledgement>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<StaffRecord> StaffRecords => Set<StaffRecord>();
    public DbSet<ServerChange> ServerChanges => Set<ServerChange>();

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

        builder.Entity<Policy>(policy =>
        {
            policy.ToTable("Policies");
            policy.HasKey(p => p.Id);
            policy.Property(p => p.Title).HasMaxLength(120).IsRequired();
            policy.Property(p => p.Category).HasMaxLength(40).IsRequired();
            policy.Property(p => p.Body).HasMaxLength(50_000).IsRequired();
        });

        builder.Entity<PolicyAcknowledgement>(ack =>
        {
            ack.ToTable("PolicyAcknowledgements");
            ack.HasKey(a => new { a.PolicyId, a.UserId });
            ack.HasOne<Policy>().WithMany().HasForeignKey(a => a.PolicyId).OnDelete(DeleteBehavior.Cascade);
            ack.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AttendanceRecord>(attendance =>
        {
            attendance.ToTable("AttendanceRecords");
            attendance.HasKey(a => a.Id);
            attendance.Property(a => a.EmployeeEmail).HasMaxLength(254).IsRequired();
            attendance.Property(a => a.EmployeeName).HasMaxLength(60).IsRequired();
            attendance.Property(a => a.ClosedBy).HasMaxLength(60);
            attendance.HasIndex(a => a.BusinessDate);
            attendance.HasIndex(a => a.EmployeeEmail);
        });

        builder.Entity<Customer>(customer =>
        {
            customer.ToTable("Customers");
            customer.HasKey(c => c.Id);
            customer.Property(c => c.Phone).HasMaxLength(20).IsRequired();
            customer.HasIndex(c => c.Phone).IsUnique();
            customer.Property(c => c.Name).HasMaxLength(60);
            customer.Property(c => c.Notes).HasMaxLength(1000);
            customer.Property(c => c.UpdatedByName).HasMaxLength(60);
        });

        builder.Entity<ServerChange>(change =>
        {
            change.ToTable("ServerChanges");
            change.HasKey(c => c.Sequence);
            change.Property(c => c.Sequence).ValueGeneratedOnAdd();
            change.Property(c => c.EntityType).HasMaxLength(20).IsRequired();
            change.Property(c => c.ProtectedPayload).IsRequired();
        });

        builder.Entity<SyncedOrder>(order =>
        {
            order.HasIndex(o => o.CustomerId);
            order.HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<StaffRecord>(profile =>
        {
            profile.ToTable("StaffRecords");
            profile.HasKey(p => p.Id);
            profile.Property(p => p.Name).HasMaxLength(60).IsRequired();
            profile.Property(p => p.Email).HasMaxLength(254).IsRequired();
            profile.HasIndex(p => p.Email);
            profile.Property(p => p.Role).HasMaxLength(20).IsRequired();
            profile.Property(p => p.UpdatedByName).HasMaxLength(60);
            profile.Property(p => p.Position).HasMaxLength(60);
            profile.Property(p => p.Phone).HasMaxLength(20);
            profile.Property(p => p.Address).HasMaxLength(300);
            profile.Property(p => p.NationalIdProtected).HasMaxLength(500);
            profile.Property(p => p.EmergencyContactName).HasMaxLength(60);
            profile.Property(p => p.EmergencyContactPhone).HasMaxLength(20);
            profile.Property(p => p.Notes).HasMaxLength(1000);
        });
    }
}
