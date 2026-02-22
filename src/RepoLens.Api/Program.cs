using System.Text.Json.Serialization;
using RepoLens.Analysis;
using RepoLens.Engine;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAnalysisServices();
builder.Services.AddEngineServices();

// CORS for React dev server
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

// Serve the React SPA from wwwroot in production
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Fallback: serve index.html for SPA routes not matched by the API
app.MapFallbackToFile("index.html");

app.Run();
