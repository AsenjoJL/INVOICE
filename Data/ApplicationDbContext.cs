using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using HazelInvoice.Models;

namespace HazelInvoice.Data;

public class ApplicationDbContext : IdentityDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products { get; set; }
    public DbSet<Service> Services { get; set; }
    public DbSet<WeeklyPrice> WeeklyPrices { get; set; }
    public DbSet<Receipt> Receipts { get; set; }
    public DbSet<ReceiptLine> ReceiptLines { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<ReceiptSequence> ReceiptSequences { get; set; }
    public DbSet<Expense> Expenses { get; set; }
    public DbSet<Goal> Goals { get; set; }
    public DbSet<Supply> Supplies { get; set; }
    public DbSet<ProductStockMovement> ProductStockMovements { get; set; }
    public DbSet<SupplyStockMovement> SupplyStockMovements { get; set; }
    public DbSet<Customer> Customers { get; set; }
    public DbSet<Supplier> Suppliers { get; set; }
    public DbSet<Purchase> Purchases { get; set; }
    public DbSet<PurchaseLine> PurchaseLines { get; set; }
    public DbSet<PurchasePayment> PurchasePayments { get; set; }
    public DbSet<PurchaseSequence> PurchaseSequences { get; set; }
    public DbSet<Deduction> Deductions { get; set; }
    public DbSet<PartnerPurchase> PartnerPurchases { get; set; }
    public DbSet<PartnerBalanceConfig> PartnerBalanceConfigs { get; set; }
    public DbSet<PartnerCapital> PartnerCapitals { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Receipt>().HasIndex(r => r.Date);
        builder.Entity<Receipt>().HasIndex(r => r.Status);
        builder.Entity<Receipt>().HasIndex(r => r.CustomerName);
        builder.Entity<Receipt>().HasIndex(r => r.CustomerId);
        builder.Entity<Receipt>().HasIndex(r => r.ReceiptNumber);

        builder.Entity<Receipt>()
            .HasOne(r => r.Customer)
            .WithMany()
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<ReceiptLine>().HasIndex(l => l.ProductId);
        builder.Entity<Purchase>().HasIndex(p => p.Date);
        builder.Entity<Purchase>().HasIndex(p => p.Status);
        builder.Entity<Purchase>().HasIndex(p => p.SupplierName);
        builder.Entity<Purchase>().HasIndex(p => p.SupplierId);
        builder.Entity<Purchase>().HasIndex(p => p.PurchaseNumber);

        builder.Entity<Purchase>()
            .HasOne(p => p.Supplier)
            .WithMany()
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<PurchaseLine>().HasIndex(l => l.ProductId);

        builder.Entity<Product>().HasIndex(p => p.IsActive);
        builder.Entity<Customer>().HasIndex(c => c.IsActive);
        builder.Entity<Supplier>().HasIndex(s => s.IsActive);

        builder.Entity<WeeklyPrice>().HasIndex(w => new { w.ProductId, w.EffectiveFrom, w.EffectiveTo });
        builder.Entity<ReceiptSequence>().HasIndex(s => s.Year);
        builder.Entity<PurchaseSequence>().HasIndex(s => s.Year);
    }

}
