using Microsoft.EntityFrameworkCore;
using PaperlessAI.API.Data;
using PaperlessAI.API.Queue;
using PaperlessAI.API.Services;
using PaperlessAI.API.BackgroundServices;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Database
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "data", "paperless-ai.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Settings service — reads DB first, appsettings.json as fallback
builder.Services.AddSingleton<AppSettingsService>();

// HTTP client factory for PaperlessClient (URL/token comes from DB at runtime)
builder.Services.AddTransient<DetailedHttpErrorHandler>();
builder.Services.AddHttpClient("paperless")
    .AddHttpMessageHandler<DetailedHttpErrorHandler>()
    .AddStandardResilienceHandler(options =>
    {
        // TotalRequestTimeout must be strictly > AttemptTimeout
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);

        // SamplingDuration must be >= 2× AttemptTimeout (min 4min)
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
        // Lower threshold: at 2min/request, default of 100 would take hours to open
        options.CircuitBreaker.MinimumThroughput = 3;

        // Reduce retries: 3 retries × 2min each would exceed TotalRequestTimeout
        options.Retry.MaxRetryAttempts = 1;
    });
builder.Services.AddScoped<PaperlessClient>();

// Azure services — scoped so AppDbContext (provider resolution) is available
builder.Services.AddScoped<DocumentIntelligenceService>();
builder.Services.AddScoped<OpenAIService>();
builder.Services.AddScoped<DataSeeder>();
builder.Services.AddScoped<MetadataContextBuilder>();
builder.Services.AddScoped<ChatService>();

// Queue
builder.Services.AddSingleton<DocumentProcessingChannel>();

// Background Services
builder.Services.AddHostedService<PaperlessPollingService>();
builder.Services.AddHostedService<DocumentProcessingWorker>();

// API
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Auto-migrate + seed on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync();
}

app.MapOpenApi();
app.MapScalarApiReference(options => options.Title = "PaperlessAI API");

app.UseCors();
app.MapControllers();

// Serve Angular SPA from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
