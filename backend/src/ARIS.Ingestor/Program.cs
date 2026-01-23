using ARIS.Ingestor;
using ARIS.Ingestor.Data;
using ARIS.Ingestor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;


var builder = Host.CreateApplicationBuilder(args);

// Ensure User Secrets are loaded
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}
else 
{
    // Force load for this console app since we rely on it for local dev execution
    builder.Configuration.AddUserSecrets<Program>();
}

// Configuration
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") 
                       ?? "Host=localhost;Database=aris_db;Username=aris_admin;Password=aris_password_local";

// Services
builder.Services.AddDbContext<ArisDbContext>(options =>
    options.UseNpgsql(connectionString, o => o.UseVector()));

builder.Services.AddHttpClient<OnetService>();
builder.Services.AddHttpClient<RoadmapService>();

// Semantic Kernel with Ollama
builder.Services.AddOllamaEmbeddingGenerator(
    modelId: "nomic-embed-text",
    endpoint: new Uri("http://localhost:11434"));

builder.Services.AddHostedService<IngestionWorker>();

var host = builder.Build();
host.Run();