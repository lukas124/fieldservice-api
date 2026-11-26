using FieldService.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Api.Data;

/// <summary>
/// PostgreSQL przez EF Core. Tabele tworzy <c>EnsureCreated</c> przy starcie (bez migracji). Zakres prac wizyty
/// i rezerwacje zapasu są kolumnami JSON w wierszu swojej jednostki spójności — zapis całości albo nic.
/// </summary>
public sealed class FieldServiceDbContext(DbContextOptions<FieldServiceDbContext> options) : DbContext(options)
{
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<PartStock> PartStocks => Set<PartStock>();
    public DbSet<VanStockItem> VanStock => Set<VanStockItem>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Technician>().HasKey(t => t.Email);

        model.Entity<WorkOrder>(e =>
        {
            e.HasMany(w => w.Visits).WithOne(v => v.WorkOrder).HasForeignKey(v => v.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
            e.Property(w => w.Status).HasConversion<string>().HasMaxLength(16);
        });

        model.Entity<Visit>(e =>
        {
            e.HasIndex(v => v.TechnicianEmails).HasMethod("gin");
            e.HasIndex(v => v.PlannedStart);
            // enumy jako tekst — czytelne w bazie i odporne na zmianę kolejności wartości
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(v => v.Outcome).HasConversion<string>().HasMaxLength(16);
            e.Property(v => v.Version).IsConcurrencyToken();
            e.OwnsMany(v => v.Activities, a => a.ToJson());
            e.OwnsMany(v => v.Parts, p => p.ToJson());
            e.OwnsOne(v => v.Signature, s => s.ToJson());
        });

        model.Entity<PartStock>(e =>
        {
            e.HasKey(s => new { s.PartId, s.WarehouseId });
            e.Property(s => s.Version).IsConcurrencyToken();
            e.OwnsMany(s => s.Reservations, r => r.ToJson());
        });

        model.Entity<VanStockItem>().HasKey(v => new { v.TechnicianEmail, v.PartId });
        model.Entity<RefreshToken>().HasKey(r => r.Hash);
        model.Entity<IdempotencyRecord>(e =>
        {
            // klucz główny (technik, klucz): drugi równoczesny zapis tego samego klucza się nie uda (409)
            e.HasKey(k => new { k.TechnicianEmail, k.Key });
            e.Property(k => k.Key).HasMaxLength(100);
        });
    }
}
