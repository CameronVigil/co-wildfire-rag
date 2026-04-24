using CoWildfireApi.Models;
using Microsoft.EntityFrameworkCore;

namespace CoWildfireApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<FireEvent> FireEvents => Set<FireEvent>();
    public DbSet<H3Cell> H3Cells => Set<H3Cell>();
    public DbSet<H3RiskHistory> H3RiskHistory => Set<H3RiskHistory>();
    public DbSet<FireEventH3Intersection> FireEventH3Intersections => Set<FireEventH3Intersection>();
    public DbSet<IngestionLog> IngestionLogs => Set<IngestionLog>();

    // Phase 5
    public DbSet<StateBoundary> StateBoundaries => Set<StateBoundary>();
    public DbSet<CoCounty> CoCounties => Set<CoCounty>();
    public DbSet<ActiveFireDetection> ActiveFireDetections => Set<ActiveFireDetection>();
    public DbSet<SmokeEvent> SmokeEvents => Set<SmokeEvent>();
    public DbSet<AqiObservation> AqiObservations => Set<AqiObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // PostGIS extension
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("uuid-ossp");

        // FireEvent — composite unique on fire_id handled by DB UNIQUE constraint
        modelBuilder.Entity<FireEvent>(e =>
        {
            // centroid is a generated column (STORED), read-only in EF
            e.Property(f => f.Centroid)
             .HasComputedColumnSql("ST_Centroid(perimeter)", stored: true)
             .ValueGeneratedOnAddOrUpdate();
        });

        // FireEventH3Intersection — composite primary key
        modelBuilder.Entity<FireEventH3Intersection>(e =>
        {
            e.HasKey(x => new { x.FireEventId, x.H3CellId });

            e.HasOne(x => x.FireEvent)
             .WithMany(f => f.H3Intersections)
             .HasForeignKey(x => x.FireEventId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.H3Cell)
             .WithMany(c => c.FireIntersections)
             .HasForeignKey(x => x.H3CellId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // H3RiskHistory — no navigation props needed
        modelBuilder.Entity<H3RiskHistory>(e =>
        {
            e.HasIndex(r => new { r.H3Index, r.ScoredAt });
            e.HasIndex(r => r.ScoredAt);
        });

        // Phase 5 — ActiveFireDetection.Location is a DB-generated STORED column
        modelBuilder.Entity<ActiveFireDetection>(e =>
        {
            e.Property(d => d.Location)
             .HasComputedColumnSql("ST_SetSRID(ST_MakePoint(longitude, latitude), 4326)", stored: true)
             .ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<StateBoundary>(e =>
        {
            e.HasIndex(s => s.StateFips).IsUnique();
            e.HasIndex(s => s.StateAbbr).IsUnique();
        });

        modelBuilder.Entity<CoCounty>(e =>
        {
            e.HasIndex(c => c.CountyFips).IsUnique();
        });

        modelBuilder.Entity<AqiObservation>(e =>
        {
            e.HasIndex(a => new { a.H3Index, a.ObservedAt }).IsUnique();
        });
    }
}
