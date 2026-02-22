using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RepoLens.Shared.Contracts;
using RepoLens.Shared.Models;

namespace RepoLens.Analysis;

/// <summary>
/// Generates rich natural-language repository summaries via the OpenAI Chat Completions API.
/// Compatible with any OpenAI-compatible endpoint (OpenAI, Azure OpenAI, Ollama, LM Studio).
/// Falls back gracefully (returns null) when no API key is available or the call fails.
/// </summary>
public class OpenAiSummaryEnricher : ISummaryEnricher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiSummaryEnricher> _logger;

    /// <summary>Server-side API key from environment variable.</summary>
    private readonly string? _configuredApiKey;

    /// <summary>Base URL for the chat completions endpoint.</summary>
    private readonly string _baseUrl;

    /// <summary>Model to use for generation.</summary>
    private readonly string _model;

    /// <summary>Max README characters to include in the prompt.</summary>
    private const int MaxReadmeChars = 3000;

    public OpenAiSummaryEnricher(HttpClient httpClient, ILogger<OpenAiSummaryEnricher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuredApiKey = Environment.GetEnvironmentVariable("REPOLENS_OPENAI_API_KEY");
        _baseUrl = Environment.GetEnvironmentVariable("REPOLENS_OPENAI_BASE_URL")
                   ?? "https://api.openai.com/v1";
        _model = Environment.GetEnvironmentVariable("REPOLENS_OPENAI_MODEL")
                 ?? "gpt-4o-mini";
    }

    public async Task<string?> EnrichAsync(
        RepositoryOverview overview,
        string? readmeContent,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var key = apiKey ?? _configuredApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogDebug("No OpenAI API key configured — skipping LLM summary");
            return null;
        }

        try
        {
            var prompt = BuildPrompt(overview, readmeContent);
            _logger.LogInformation("Calling OpenAI ({Model}) for {Name}...", _model, overview.Name);
            var summary = await CallChatCompletionAsync(key, prompt, cancellationToken);
            _logger.LogInformation("LLM summary generated for {Name} ({Length} chars)",
                overview.Name, summary?.Length ?? 0);
            return summary;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "OpenAI HTTP error ({Status}) — falling back to template. Details: {Message}",
                ex.StatusCode, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM summary generation failed — falling back to template. Details: {Message}",
                ex.Message);
            return null;
        }
    }

    private string BuildPrompt(RepositoryOverview overview, string? readmeContent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert software engineer. Write a concise but informative summary (3-5 paragraphs) of the following GitHub repository. Explain what the project does, its architecture, key technologies, and anything notable. Write in third person.");
        sb.AppendLine();
        sb.AppendLine($"Repository: {overview.Name}");
        sb.AppendLine($"URL: {overview.Url}");
        sb.AppendLine($"Complexity: {overview.Complexity}");
        sb.AppendLine($"Total files: {overview.TotalFiles:N0}");
        sb.AppendLine($"Total lines: {overview.TotalLines:N0}");
        sb.AppendLine();

        // Languages
        if (overview.LanguageBreakdown.Count > 0)
        {
            sb.AppendLine("Languages (by file count):");
            foreach (var (lang, count) in overview.LanguageBreakdown)
                sb.AppendLine($"  - {lang}: {count} files");
        }

        // Frameworks
        if (overview.DetectedFrameworks.Count > 0)
            sb.AppendLine($"Frameworks: {string.Join(", ", overview.DetectedFrameworks)}");

        // Entry points
        if (overview.EntryPoints.Count > 0)
            sb.AppendLine($"Entry points: {string.Join(", ", overview.EntryPoints)}");

        // Key types
        if (overview.KeyTypes.Count > 0)
        {
            sb.AppendLine("Key types:");
            foreach (var kt in overview.KeyTypes.Take(8))
                sb.AppendLine($"  - {kt.Name} ({kt.Kind}, {kt.MemberCount} members) in {kt.FilePath}");
        }

        // External dependencies
        if (overview.ExternalDependencies.Count > 0)
        {
            sb.AppendLine($"External dependencies: {string.Join(", ", overview.ExternalDependencies.Take(20))}");
        }

        // Top-level folders
        if (overview.TopLevelFolders.Count > 0)
            sb.AppendLine($"Top-level folders: {string.Join(", ", overview.TopLevelFolders)}");

        // Symbol counts
        if (overview.SymbolCounts.Count > 0)
        {
            var counts = string.Join(", ", overview.SymbolCounts.Select(kv => $"{kv.Value} {kv.Key}s"));
            sb.AppendLine($"Symbols: {counts}");
        }

        // README excerpt
        if (!string.IsNullOrWhiteSpace(readmeContent))
        {
            var excerpt = readmeContent.Length > MaxReadmeChars
                ? readmeContent[..MaxReadmeChars] + "\n[...truncated]"
                : readmeContent;
            sb.AppendLine();
            sb.AppendLine("README.md:");
            sb.AppendLine(excerpt);
        }

        return sb.ToString();
    }

    private async Task<string?> CallChatCompletionAsync(
        string apiKey, string prompt, CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl.TrimEnd('/')}/chat/completions";

        var requestBody = new ChatCompletionRequest
        {
            Model = _model,
            Messages =
            [
                new ChatMessage { Role = "system", Content = "You are a helpful assistant that summarizes software repositories. Be concise, specific, and technical. Do not use markdown formatting — output plain text only." },
                new ChatMessage { Role = "user", Content = prompt }
            ],
            MaxCompletionTokens = 600,
            Temperature = 0.3
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("OpenAI API returned {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode(); // throws with status code
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson, JsonOptions);

        return completion?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
    }

    // ─── JSON models for OpenAI Chat Completions API ───────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class ChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public List<ChatMessage> Messages { get; set; } = [];
        public int? MaxCompletionTokens { get; set; }
        public double Temperature { get; set; } = 0.3;
    }

    private class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private class ChatCompletionResponse
    {
        public List<ChatChoice>? Choices { get; set; }
    }

    private class ChatChoice
    {
        public ChatMessage? Message { get; set; }
    }
}
