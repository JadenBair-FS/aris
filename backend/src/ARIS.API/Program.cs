using ARIS.Shared.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using QuestPDF.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Scalar.AspNetCore;
using OllamaSharp;
using ElBruno.OllamaSharp.Extensions;
using Serilog;

QuestPDF.Settings.License = LicenseType.Community;

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

// Semantic Kernel (Unified Ollama)
var ollamaUriString = builder.Configuration["Ollama:Uri"] ?? "http://localhost:11434";
var ollamaUri = new Uri(ollamaUriString);

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

// HttpClient for Clerk Backend API
builder.Services.AddHttpClient("Clerk", client =>
{
    client.BaseAddress = new Uri("https://api.clerk.com/v1/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Domain Services
builder.Services.AddScoped<ARIS.API.Services.DictionaryService>();
builder.Services.AddScoped<ARIS.API.Services.ResumeService>();
builder.Services.AddScoped<ARIS.API.Services.JobService>();
builder.Services.AddScoped<ARIS.API.Services.MatchService>();
builder.Services.AddSingleton<ARIS.API.Services.GraphService>();
builder.Services.AddScoped<ARIS.API.Services.GroundingService>();
builder.Services.AddScoped<ARIS.API.Services.ExtractionBenchmarkService>();
builder.Services.AddScoped<ARIS.API.Services.PersonalInfoExtractor>();
builder.Services.AddScoped<ARIS.API.Services.ResumePdfService>();
builder.Services.AddScoped<ARIS.API.Services.OntologyExpansionService>();

// Auth — Clerk JWT Bearer
var clerkAuthority = builder.Configuration["Clerk:Authority"]
    ?? throw new InvalidOperationException("Clerk:Authority is not configured in appsettings.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = clerkAuthority;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
        };
    });

builder.Services.AddAuthorization();

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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ArisDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
