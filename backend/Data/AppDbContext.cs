using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<RawExtraction> RawExtractions => Set<RawExtraction>();
    public DbSet<AnalyticsExtraction> AnalyticsExtractions => Set<AnalyticsExtraction>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<ManufacturerPromptHistory> ManufacturerPromptHistory => Set<ManufacturerPromptHistory>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

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

        modelBuilder.Entity<Manufacturer>(entity =>
        {
            entity.HasKey(m => m.Id);
            // Case-insensitive uniqueness at the SQL Server default collation would already dedupe
            // "Rheem" vs "rheem", but an explicit index also makes the intent visible and gives a
            // clean constraint-violation error instead of a silent duplicate row.
            entity.HasIndex(m => m.Name).IsUnique();
            entity.Property(m => m.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<ManufacturerPromptHistory>(entity =>
        {
            entity.HasKey(h => h.Id);
            entity.Property(h => h.Instructions).HasColumnType("nvarchar(max)");
            entity.HasIndex(h => new { h.ManufacturerId, h.CreatedDate });

            // No navigation collection on Manufacturer, no cascade-configured delete needed beyond
            // the default (Cascade) — removing a manufacturer from the picker should take its prompt
            // history with it rather than leave orphaned rows.
            entity.HasOne(h => h.Manufacturer)
                  .WithMany()
                  .HasForeignKey(h => h.ManufacturerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(100);
        });
    }
}