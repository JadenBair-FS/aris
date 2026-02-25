using ARIS.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;
using Scalar.AspNetCore;
using OllamaSharp;
using ElBruno.OllamaSharp.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("Logs/aris-api-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<ArisDbContext>(options =>
    options.UseNpgsql(dataSource, o => o.UseVector()));

// Semantic Kernel
var ollamaUri = new Uri("http://192.168.4.172:11434");

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

// Domain Services
builder.Services.AddScoped<ARIS.API.Services.DictionaryService>();
builder.Services.AddScoped<ARIS.API.Services.ResumeService>();
builder.Services.AddScoped<ARIS.API.Services.JobService>();
builder.Services.AddScoped<ARIS.API.Services.MatchService>();
builder.Services.AddSingleton<ARIS.API.Services.GraphService>();
builder.Services.AddScoped<ARIS.API.Services.GroundingService>();
builder.Services.AddScoped<ARIS.API.Services.ExtractionBenchmarkService>();

// CORS
builder.Services.AddCors(options =>
{ 
    options.AddPolicy("AllowAll",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.MapControllers();

app.Run();