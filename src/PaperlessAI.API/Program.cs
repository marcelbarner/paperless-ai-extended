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
    .AddHttpMessageHandler<DetailedHttpErrorHandler>();
builder.Services.AddScoped<PaperlessClient>();

// Azure services — credentials read from DB at call time
builder.Services.AddSingleton<DocumentIntelligenceService>();
builder.Services.AddSingleton<OpenAIService>();
builder.Services.AddScoped<MetadataContextBuilder>();

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

// Auto-migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
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
