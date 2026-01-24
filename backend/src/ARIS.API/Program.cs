using ARIS.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ArisDbContext>(options =>
    options.UseNpgsql(connectionString, o => o.UseVector()));

// Semantic Kernel (Replaced with direct MEAI)
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new OllamaEmbeddingGenerator(new Uri("http://localhost:11434"), "all-minilm"));

builder.Services.AddSingleton<IChatClient>(sp =>
    new OllamaChatClient(new Uri("http://localhost:11434"), "llama3.1"));

// Domain Services
builder.Services.AddScoped<ARIS.API.Services.DictionaryService>();

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