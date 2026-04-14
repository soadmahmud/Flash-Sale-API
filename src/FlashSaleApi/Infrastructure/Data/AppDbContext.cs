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
        var now = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<FlashSaleProduct>().HasData(
            new FlashSaleProduct
            {
                Id = 1,
                Name = "Sony WH-1000XM5 Headphones",
                Description = "Industry-leading noise cancelling wireless headphones",
                OriginalPrice = 399.99m,
                DiscountPrice = 249.99m,
                StockQuantity = 500,
                StartTime = now.AddHours(-1),
                EndTime = now.AddHours(5),
                ImageUrl = "https://placeholder.com/headphones.jpg",
                CreatedAt = now
            },
            new FlashSaleProduct
            {
                Id = 2,
                Name = "Samsung Galaxy S24 Ultra",
                Description = "1TB | 12GB RAM | 200MP Camera | AI-powered flagship",
                OriginalPrice = 1299.99m,
                DiscountPrice = 899.99m,
                StockQuantity = 200,
                StartTime = now,
                EndTime = now.AddHours(3),
                ImageUrl = "https://placeholder.com/s24ultra.jpg",
                CreatedAt = now
            },
            new FlashSaleProduct
            {
                Id = 3,
                Name = "Apple AirPods Pro (3rd Gen)",
                Description = "Adaptive Audio, ANC, USB-C charging case",
                OriginalPrice = 249.99m,
                DiscountPrice = 179.99m,
                StockQuantity = 1000,
                StartTime = now.AddMinutes(-30),
                EndTime = now.AddHours(4),
                ImageUrl = "https://placeholder.com/airpodspro.jpg",
                CreatedAt = now
            }
        );
    }
}
