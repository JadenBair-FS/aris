using ARIS.Ingestor;
using ARIS.Shared.Data;
using ARIS.Ingestor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using OllamaSharp;
using ElBruno.OllamaSharp.Extensions;
using Npgsql;
using Serilog;
using Serilog.Events;

// Bootstrap logger for startup errors (replaced after host builds)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = Host.CreateApplicationBuilder(args);

// Ensure User Secrets are loaded
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}
else
{
    builder.Configuration.AddUserSecrets<Program>();
}

// Configuration
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                       ?? "Host=localhost;Database=aris_db;Username=aris_admin;Password=aris_password_local";

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();

// Services
builder.Services.AddDbContext<ArisDbContext>(options =>
    options.UseNpgsql(dataSource, o => o.UseVector()));

builder.Services.AddHttpClient<OnetService>();
builder.Services.AddHttpClient<RoadmapService>();
builder.Services.AddSingleton<Neo4jIngestionService>();

// AI - MEAI with Ollama
var ollamaUri = new Uri(
    Environment.GetEnvironmentVariable("Ollama__BaseUrl") ?? "http://localhost:11434");

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var client = new OllamaApiClient(ollamaUri, "qwen3-embedding:0.6b");
    client.SetTimeout(TimeSpan.FromHours(1));
    return client;
});

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var client = new OllamaApiClient(ollamaUri, "mistral");
    client.SetTimeout(TimeSpan.FromHours(1));
    return client;
});

builder.Services.AddTransient<OntologyEnrichmentService>();

builder.Services.AddScoped<GoldStandardSeeder>();
builder.Services.AddHostedService<IngestionWorker>();

// Serilog: replace default Microsoft logging
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

builder.Services.AddSerilog((services, config) => config
    .MinimumLevel.Debug()
    // Suppress noisy framework namespaces
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    // Console: colorized, human-readable
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    // File: full detail, daily rolling, kept for 14 days
    .WriteTo.File(
        path: Path.Combine(logDir, "ingestor-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext());

var host = builder.Build();

if (args.Contains("--seed-gold-standard"))
{
    using (var scope = host.Services.CreateScope())
    {
        var seeder = scope.ServiceProvider.GetRequiredService<GoldStandardSeeder>();
        var db = scope.ServiceProvider.GetRequiredService<ArisDbContext>();

        // Ensure DB exists
        await db.Database.EnsureCreatedAsync();

        // Path Resolution
        var currentDir = Directory.GetCurrentDirectory();
        // Check if we are deep in bin/Debug
        string relativePath = currentDir.Contains("bin") ? "../../../../../../Datasets/GoldStandard" : "../../../Datasets/GoldStandard";

        var rootDir = new DirectoryInfo(currentDir);
        while (rootDir != null && !rootDir.GetDirectories("Datasets").Any())
        {
            rootDir = rootDir.Parent;
        }

        if (rootDir == null)
        {
            // Fallback: Hardcode for this environment
            rootDir = new DirectoryInfo(@"C:\dev\Masters Capstone\Development");
        }

        var datasetPath = Path.Combine(rootDir.FullName, "Datasets", "GoldStandard");

        Console.WriteLine($"Running Gold Standard Seeder using data from: {datasetPath}");
        await seeder.RunSeedingAsync(datasetPath);
    }
}
else
{
    host.Run();
}