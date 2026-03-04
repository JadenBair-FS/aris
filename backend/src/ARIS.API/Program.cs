using ARIS.Shared.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using QuestPDF.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Scalar.AspNetCore;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using System.ClientModel;
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

// Semantic Kernel (Llama.cpp OpenAI Compatible)
var mistralUriString = builder.Configuration["LlamaCpp:MistralUri"] ?? "http://localhost:11434/v1";
var qwenUriString = builder.Configuration["LlamaCpp:QwenUri"] ?? "http://localhost:11435/v1";

var mistralUri = new Uri(mistralUriString);
var qwenUri = new Uri(qwenUriString);

// Chat Client for Mistral
var chatClient = new ChatClient("mistral-7b-v0.3", new ApiKeyCredential("no-key"), new OpenAIClientOptions { Endpoint = mistralUri });
builder.Services.AddChatClient(chatClient.AsIChatClient());

// Embedding Client for Qwen
var embeddingClient = new EmbeddingClient("qwen3-0.6b-embedding", new ApiKeyCredential("no-key"), new OpenAIClientOptions { Endpoint = qwenUri });
builder.Services.AddEmbeddingGenerator(embeddingClient.AsIEmbeddingGenerator());

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

app.Run();
