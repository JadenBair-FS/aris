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

// Ollama
var ollamaUriString = builder.Configuration["Ollama:Uri"] ?? "http://localhost:11434";
var ollamaUri = new Uri(ollamaUriString);

var chatModel = builder.Configuration["Ollama:ChatModel"] ?? "mistral";
var extractionModel = builder.Configuration["Ollama:ExtractionModel"] ?? "nuextract:latest";
var embeddingModel = builder.Configuration["Ollama:EmbeddingModel"] ?? "qwen3-embedding:0.6b";
var numCtx = builder.Configuration.GetValue<int>("Ollama:NumCtx", 4096);

var groundingFirstPassThreshold = builder.Configuration.GetValue<double>("Grounding:FirstPassThreshold", 0.10);
var groundingSecondPassThreshold = builder.Configuration.GetValue<double>("Grounding:SecondPassThreshold", 0.35);

Log.Information("Ollama: {Uri} | Chat: {ChatModel} | Extraction: {ExtractionModel} | Embedding: {EmbeddingModel} | NumCtx: {NumCtx}",
    ollamaUriString, chatModel, extractionModel, embeddingModel, numCtx);
Log.Information("Grounding thresholds — Pass 1 (all skills): {First}, Pass 2 (soft skills only): {Second}", groundingFirstPassThreshold, groundingSecondPassThreshold);

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var client = new OllamaApiClient(ollamaUri, embeddingModel);
    client.SetTimeout(TimeSpan.FromHours(1));
    return client;
});

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var inner = new OllamaApiClient(ollamaUri, chatModel);
    inner.SetTimeout(TimeSpan.FromHours(1));
    return new NumCtxChatClient(inner, numCtx);
});

var ollamaGenerateUrl = $"{ollamaUriString.TrimEnd('/')}/api/generate";

// HttpClient for Clerk Backend API
builder.Services.AddHttpClient("Clerk", client =>
{
    client.BaseAddress = new Uri("https://api.clerk.com/v1/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Domain Services
builder.Services.AddScoped<ARIS.API.Services.DictionaryService>();
builder.Services.AddScoped<ARIS.API.Services.ResumeService>(sp =>
    new ARIS.API.Services.ResumeService(
        sp.GetRequiredService<ARIS.Shared.Data.ArisDbContext>(),
        sp.GetRequiredService<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(),
        sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>(),
        sp.GetRequiredService<ARIS.API.Services.PersonalInfoExtractor>(),
        ollamaGenerateUrl,
        extractionModel,
        sp.GetRequiredService<ILogger<ARIS.API.Services.ResumeService>>(),
        groundingFirstPassThreshold,
        groundingSecondPassThreshold,
        numCtx));
builder.Services.AddScoped<ARIS.API.Services.JobService>(sp =>
    new ARIS.API.Services.JobService(
        sp.GetRequiredService<ARIS.Shared.Data.ArisDbContext>(),
        sp.GetRequiredService<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(),
        sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>(),
        ollamaGenerateUrl,
        extractionModel,
        sp.GetRequiredService<ILogger<ARIS.API.Services.JobService>>(),
        groundingFirstPassThreshold,
        groundingSecondPassThreshold,
        numCtx));
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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ArisDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();

/// <summary>
/// Wraps an IChatClient to inject a fixed num_ctx (context window size) into every request,
/// sourced from appsettings.json so it can be changed without rebuilding.
/// </summary>
file sealed class NumCtxChatClient(IChatClient inner, int numCtx) : DelegatingChatClient(inner)
{
#pragma warning disable CS8765
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken)
    {
        var opts = (options ?? new ChatOptions()).Clone();
        (opts.AdditionalProperties ??= new())["num_ctx"] = numCtx;
        return base.GetResponseAsync(messages, opts, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken)
    {
        var opts = (options ?? new ChatOptions()).Clone();
        (opts.AdditionalProperties ??= new())["num_ctx"] = numCtx;
        return base.GetStreamingResponseAsync(messages, opts, cancellationToken);
    }
#pragma warning restore CS8765
}
