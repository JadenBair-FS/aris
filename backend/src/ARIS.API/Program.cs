using ARIS.Shared.Data;
using Microsoft.EntityFrameworkCore;
using OllamaSharp.Models;
using Microsoft.Extensions.AI;
using Npgsql;
using Scalar.AspNetCore;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new OllamaApiClient(new Uri("http://localhost:11434"), "all-minilm"));

builder.Services.AddSingleton<IChatClient>(sp =>
    new OllamaApiClient(new Uri("http://localhost:11434"), "llama3.1"));

// Domain Services
builder.Services.AddScoped<ARIS.API.Services.DictionaryService>();
builder.Services.AddScoped<ARIS.API.Services.ResumeService>();

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