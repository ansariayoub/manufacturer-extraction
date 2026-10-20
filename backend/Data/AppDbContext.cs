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
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(200);
            entity.HasIndex(c => c.Name).IsUnique();
            entity.HasData(new Client
            {
                Id = Client.DefaultId,
                Name = "Default client",
                CreatedDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
            });
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email).HasMaxLength(320);
            entity.Property(u => u.ObjectId).HasMaxLength(64);
            entity.Property(u => u.DisplayName).HasMaxLength(200);
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasOne(u => u.Client)
                  .WithMany()
                  .HasForeignKey(u => u.ClientId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.ProcessingStatus).HasConversion<string>();

            // Every listing is scoped to one client first.
            entity.HasIndex(d => d.ClientId);
            entity.HasOne(d => d.Client)
                  .WithMany()
                  .HasForeignKey(d => d.ClientId)
                  .OnDelete(DeleteBehavior.Restrict);

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
            // clean constraint-violation error instead of a silent duplicate row. Unique PER CLIENT:
            // two clients may each have their own "Rheem".
            entity.HasIndex(m => new { m.ClientId, m.Name }).IsUnique();
            entity.Property(m => m.Name).HasMaxLength(200);
            entity.HasOne(m => m.Client)
                  .WithMany()
                  .HasForeignKey(m => m.ClientId)
                  .OnDelete(DeleteBehavior.Restrict);
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