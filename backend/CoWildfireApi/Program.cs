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
    builder.Services.AddDbContextFactory<AppDbContext>(o =>
        o.UseNpgsql(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            npgsql => npgsql.UseNetTopologySuite()));

    builder.Services.AddScoped<AppDbContext>(sp =>
        sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

    // ── Qdrant ────────────────────────────────
    builder.Services.AddSingleton<QdrantClient>(_ =>
    {
        var host = builder.Configuration["Qdrant:Host"] ?? "localhost";
        var port = builder.Configuration.GetValue<int>("Qdrant:Port", 6334);
        return new QdrantClient(host, port);
    });

    // ── HTTP clients ──────────────────────────
    builder.Services.AddHttpClient();

    // Named "noaa" client: NOAA Weather.gov requires a User-Agent header (returns 403 without it)
    builder.Services.AddHttpClient("noaa", client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CoWildfireAnalyzer/1.0 (contact@cowildfire.dev)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json,application/json");
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    // ── Services ──────────────────────────────
    // Singletons: hold in-memory caches that must survive across request scopes
    builder.Services.AddSingleton<NoaaService>();
    builder.Services.AddSingleton<RawsService>();
    builder.Services.AddSingleton<DroughtService>();

    // Singletons: hold in-memory state (dedup sets, caches, SSE channels)
    builder.Services.AddSingleton<FeedService>();
    builder.Services.AddSingleton<OriginClassifierService>();
    builder.Services.AddSingleton<FirmsService>();
    builder.Services.AddSingleton<AirNowService>();
    builder.Services.AddSingleton<HmsService>();

    // Scoped / transient: use IDbContextFactory; resolved fresh per scoring run
    builder.Services.AddScoped<H3GridService>();
    builder.Services.AddScoped<MtbsIngester>();
    builder.Services.AddTransient<RiskScoringService>();
    builder.Services.AddScoped<RagService>();
    builder.Services.AddScoped<EmbeddingService>();
    builder.Services.AddScoped<InciwebIngester>();

    // Background services
    builder.Services.AddHostedService<RiskScoringBackgroundService>();
    builder.Services.AddHostedService<FeedPollingBackgroundService>();

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

    // ── Startup: ensure Qdrant "wildfire_docs" collection exists ─
    try
    {
        var qdrant = app.Services.GetRequiredService<QdrantClient>();
        var collections = await qdrant.ListCollectionsAsync();
        bool exists = collections.Any(c => c == "wildfire_docs");
        if (!exists)
        {
            await qdrant.CreateCollectionAsync("wildfire_docs",
                new Qdrant.Client.Grpc.VectorParams
                {
                    Size     = 768,  // nomic-embed-text output dimension
                    Distance = Qdrant.Client.Grpc.Distance.Cosine,
                });
            Log.Information("Qdrant collection 'wildfire_docs' created (768-dim cosine)");
        }
        else
        {
            Log.Information("Qdrant collection 'wildfire_docs' already exists — skipping creation");
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Qdrant collection init skipped (Qdrant may not be available): {Message}", ex.Message);
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
