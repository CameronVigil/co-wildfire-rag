using CoWildfireApi.Data;
using CoWildfireApi.Ingestion;
using CoWildfireApi.Services;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.IO.Converters;
using Qdrant.Client;
using Serilog;

// ──────────────────────────────────────────────
// Serilog bootstrap
// ──────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ──────────────────────────────
    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console());

    // ── CORS ─────────────────────────────────
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? new[] { "http://localhost:5173", "http://localhost:3000" };

    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins(allowedOrigins)
         .AllowAnyHeader()
         .AllowAnyMethod()));

    // ── PostgreSQL + PostGIS (EF Core) ────────
    builder.Services.AddDbContext<AppDbContext>(o =>
        o.UseNpgsql(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            npgsql => npgsql.UseNetTopologySuite()));

    builder.Services.AddDbContextFactory<AppDbContext>(o =>
        o.UseNpgsql(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            npgsql => npgsql.UseNetTopologySuite()));

    // ── Qdrant ────────────────────────────────
    builder.Services.AddSingleton<QdrantClient>(_ =>
    {
        var host = builder.Configuration["Qdrant:Host"] ?? "localhost";
        var port = builder.Configuration.GetValue<int>("Qdrant:Port", 6334);
        return new QdrantClient(host, port);
    });

    // ── HTTP clients ──────────────────────────
    builder.Services.AddHttpClient();

    // ── Services ──────────────────────────────
    builder.Services.AddScoped<H3GridService>();
    builder.Services.AddScoped<MtbsIngester>();
    builder.Services.AddScoped<RiskScoringService>();
    builder.Services.AddScoped<RagService>();
    builder.Services.AddScoped<NoaaService>();
    builder.Services.AddScoped<FirmsService>();
    builder.Services.AddScoped<EmbeddingService>();

    // ── Controllers with GeoJSON serialisation ─
    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.Converters.Add(new GeoJsonConverterFactory());
        });

    // ── Swagger ───────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "Colorado Wildfire RAG API", Version = "v1-phase1" });
    });

    // ──────────────────────────────────────────
    var app = builder.Build();
    // ──────────────────────────────────────────

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseSerilogRequestLogging();
    app.UseCors();
    app.UseAuthorization();
    app.MapControllers();

    // ── Startup: seed H3 grid if h3_cells empty ─
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var gridService = scope.ServiceProvider.GetRequiredService<H3GridService>();
            await gridService.SeedGridIfEmptyAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "H3 grid seeding skipped (DB may not be available): {Message}", ex.Message);
        }
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup failed");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
