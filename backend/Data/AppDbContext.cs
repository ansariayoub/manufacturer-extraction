using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<RawExtraction> RawExtractions => Set<RawExtraction>();
    public DbSet<AnalyticsExtraction> AnalyticsExtractions => Set<AnalyticsExtraction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.ProcessingStatus).HasConversion<string>();

            // The queue listing is always "newest first"; without this the server sorts the whole
            // table on every request.
            entity.HasIndex(d => d.UploadDate);

            // The startup resume scan filters on status.
            entity.HasIndex(d => d.ProcessingStatus);

            entity.HasOne(d => d.RawExtraction)
                  .WithOne(r => r.Document)
                  .HasForeignKey<RawExtraction>(r => r.DocumentId);
            entity.HasOne(d => d.AnalyticsExtraction)
                  .WithOne(a => a.Document)
                  .HasForeignKey<AnalyticsExtraction>(a => a.DocumentId);
        });

        modelBuilder.Entity<RawExtraction>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.RawJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<AnalyticsExtraction>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.AnalyticsJson).HasColumnType("nvarchar(max)");
        });
    }
}