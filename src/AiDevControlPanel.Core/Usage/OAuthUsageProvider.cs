using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiDevControlPanel.Core.Usage;

public interface IUsageProvider
{
    /// <summary>Current usage, or null when it cannot be determined.</summary>
    Task<UsageSnapshot?> GetUsageAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the subscription usage the way Claude Code's /usage does: with the OAuth token from
/// ~/.claude/.credentials.json against an undocumented endpoint. The token is only sent to api.anthropic.com.
/// </summary>
public sealed class OAuthUsageProvider(HttpClient http, TimeProvider time, string? credentialsPath = null) : IUsageProvider
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly string _credentialsPath = credentialsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public async Task<UsageSnapshot?> GetUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await ReadAccessTokenAsync(ct);
            if (token is null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? ParseResponse(await response.Content.ReadAsStringAsync(ct)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    internal static UsageSnapshot? ParseResponse(string json)
    {
        var root = JsonNode.Parse(json);
        var session = ParseWindow(root?["five_hour"]);
        var weekly = ParseWindow(root?["seven_day"]);
        return session is not null && weekly is not null ? new UsageSnapshot(session, weekly) : null;
    }

    private static UsageWindow? ParseWindow(JsonNode? window)
    {
        var utilization = window?["utilization"]?.GetValue<double>();
        var resetsAt = window?["resets_at"]?.GetValue<string>();
        return utilization is { } percent && DateTimeOffset.TryParse(resetsAt, out var reset)
            ? new UsageWindow(percent, reset)
            : null;
    }

    private async Task<string?> ReadAccessTokenAsync(CancellationToken ct)
    {
        if (!File.Exists(_credentialsPath))
        {
            return null;
        }

        var oauth = JsonNode.Parse(await File.ReadAllTextAsync(_credentialsPath, ct))?["claudeAiOauth"];
        var token = oauth?["accessToken"]?.GetValue<string>();
        var expiresAt = oauth?["expiresAt"]?.GetValue<long>();

        // An expired token is refreshed by the next claude run; until then usage is unknown.
        if (expiresAt is { } ms && DateTimeOffset.FromUnixTimeMilliseconds(ms) <= time.GetUtcNow())
        {
            return null;
        }

        return string.IsNullOrEmpty(token) ? null : token;
    }
}
