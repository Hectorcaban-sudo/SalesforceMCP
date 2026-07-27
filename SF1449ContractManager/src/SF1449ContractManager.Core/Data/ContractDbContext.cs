using Microsoft.EntityFrameworkCore;
using SF1449ContractManager.Core.Models;

namespace SF1449ContractManager.Core.Data;

public class ContractDbContext : DbContext
{
    public ContractDbContext(DbContextOptions<ContractDbContext> options) : base(options) { }

    public DbSet<Sf1449Contract> Contracts => Set<Sf1449Contract>();
    public DbSet<ContractLineItem> LineItems => Set<ContractLineItem>();
    public DbSet<ContractClause> Clauses => Set<ContractClause>();
    public DbSet<FieldExtraction> FieldExtractions => Set<FieldExtraction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Sf1449Contract>(entity =>
        {
            entity.HasIndex(c => c.ContractNumber);
            entity.HasIndex(c => c.SolicitationNumber);
            entity.Property(c => c.TotalAwardAmount).HasPrecision(18, 2);
            entity.Property(c => c.SizeStandardUsd).HasPrecision(18, 2);
            entity.Property(c => c.SetAsidePercent).HasPrecision(5, 2);

            entity.HasMany(c => c.LineItems)
                  .WithOne(li => li.Sf1449Contract)
                  .HasForeignKey(li => li.Sf1449ContractId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.Clauses)
                  .WithOne(cl => cl.Sf1449Contract)
                  .HasForeignKey(cl => cl.Sf1449ContractId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.FieldExtractions)
                  .WithOne(fe => fe.Sf1449Contract)
                  .HasForeignKey(fe => fe.Sf1449ContractId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContractLineItem>(entity =>
        {
            entity.Property(li => li.Quantity).HasPrecision(18, 2);
            entity.Property(li => li.UnitPrice).HasPrecision(18, 4);
            entity.Property(li => li.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<ContractClause>(entity =>
        {
            entity.HasIndex(cl => new { cl.Sf1449ContractId, cl.ClauseNumber, cl.Section });
        });
    }
}
