using FlashSaleApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FlashSaleApi.Infrastructure.Data;

/// <summary>
/// EF Core database context.
/// Configured for PostgreSQL via Npgsql provider.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<FlashSaleProduct> FlashSaleProducts => Set<FlashSaleProduct>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── FlashSaleProduct ────────────────────────────────────────────────
        modelBuilder.Entity<FlashSaleProduct>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.Property(p => p.OriginalPrice).HasPrecision(18, 2);
            entity.Property(p => p.DiscountPrice).HasPrecision(18, 2);
            entity.Property(p => p.MaxQuantityPerUser).HasDefaultValue(5);
            entity.HasIndex(p => new { p.StartTime, p.EndTime });
        });

        // ── Order ───────────────────────────────────────────────────────────
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.UserId).HasMaxLength(100).IsRequired();
            entity.Property(o => o.TotalAmount).HasPrecision(18, 2);
            entity.Property(o => o.Status).HasConversion<string>(); // store as string
            entity.Property(o => o.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.HasIndex(o => o.UserId);
            entity.HasIndex(o => o.IdempotencyKey).IsUnique();
        });

        // ── OrderItem ───────────────────────────────────────────────────────
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(oi => oi.Id);
            entity.Property(oi => oi.UnitPrice).HasPrecision(18, 2);

            entity.HasOne(oi => oi.Order)
                  .WithMany(o => o.Items)
                  .HasForeignKey(oi => oi.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(oi => oi.Product)
                  .WithMany(p => p.OrderItems)
                  .HasForeignKey(oi => oi.ProductId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Seed data ───────────────────────────────────────────────────────
        SeedFlashSaleProducts(modelBuilder);
    }

    private static void SeedFlashSaleProducts(ModelBuilder modelBuilder)
    {
        // FIX: Use a fixed reference point that is ALWAYS in the past/present
        // so the seeded products are always active when the app is running.
        // Previously the date was hard-coded to 2026-04-15 which would expire.
        // Using a distant future end time ensures the demo data is always usable.
        var seedBase = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var alwaysActive = new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        modelBuilder.Entity<FlashSaleProduct>().HasData(
            new FlashSaleProduct
            {
                Id                = 1,
                Name              = "Sony WH-1000XM5 Headphones",
                Description       = "Industry-leading noise cancelling wireless headphones",
                OriginalPrice     = 399.99m,
                DiscountPrice     = 249.99m,
                StockQuantity     = 500,
                MaxQuantityPerUser = 3,   // max 3 per user
                StartTime         = seedBase,
                EndTime           = alwaysActive,
                ImageUrl          = "https://placeholder.com/headphones.jpg",
                CreatedAt         = seedBase
            },
            new FlashSaleProduct
            {
                Id                = 2,
                Name              = "Samsung Galaxy S24 Ultra",
                Description       = "1TB | 12GB RAM | 200MP Camera | AI-powered flagship",
                OriginalPrice     = 1299.99m,
                DiscountPrice     = 899.99m,
                StockQuantity     = 200,
                MaxQuantityPerUser = 1,   // max 1 per user (high-value item)
                StartTime         = seedBase,
                EndTime           = alwaysActive,
                ImageUrl          = "https://placeholder.com/s24ultra.jpg",
                CreatedAt         = seedBase
            },
            new FlashSaleProduct
            {
                Id                = 3,
                Name              = "Apple AirPods Pro (3rd Gen)",
                Description       = "Adaptive Audio, ANC, USB-C charging case",
                OriginalPrice     = 249.99m,
                DiscountPrice     = 179.99m,
                StockQuantity     = 1000,
                MaxQuantityPerUser = 5,   // max 5 per user
                StartTime         = seedBase,
                EndTime           = alwaysActive,
                ImageUrl          = "https://placeholder.com/airpodspro.jpg",
                CreatedAt         = seedBase
            }
        );
    }
}
