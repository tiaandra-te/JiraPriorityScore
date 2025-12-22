using System.Text;
using System.Text.Json;
using JiraPriorityScore.Models;

namespace JiraPriorityScore.Services;

public class JiraClient
{
    private readonly HttpClient _httpClient;
    private readonly JiraSettings _settings;

    public JiraClient(HttpClient httpClient, JiraSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    public async Task<IssuePage> LoadIssuesPageAsync(string[] fields, int startAt, int maxResults)
    {
        var jql = $"filter={_settings.FilterId}";
        var fieldsParam = fields.Length == 0 ? "summary" : string.Join(",", fields);
        var url = $"rest/api/{_settings.ApiVersion}/search/jql?jql={Uri.EscapeDataString(jql)}&startAt={startAt}&maxResults={maxResults}&fields={Uri.EscapeDataString(fieldsParam)}";
        Console.WriteLine($"Jira Request: GET {new Uri(_httpClient.BaseAddress!, url)}");

        using var response = await _httpClient.GetAsync(url);
        var rawBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Failed to load filter {_settings.FilterId}: {(int)response.StatusCode} {rawBody}");
        }

        using var doc = JsonDocument.Parse(rawBody);
        var issues = new List<JiraIssue>();
        var total = 0;
        var totalKnown = false;

        if (doc.RootElement.TryGetProperty("total", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
        {
            total = totalProp.GetInt32();
            totalKnown = true;
        }

        if (doc.RootElement.TryGetProperty("issues", out var issuesElement) && issuesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var issueElement in issuesElement.EnumerateArray())
            {
                var key = issueElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var fieldsElement = issueElement.TryGetProperty("fields", out var fieldsProp)
                    ? fieldsProp.Clone()
                    : new JsonElement();

                issues.Add(new JiraIssue(key, fieldsElement));
            }
        }

        if (totalKnown && total == 0 && issues.Count > 0)
        {
            totalKnown = false;
        }

        if (!totalKnown && issues.Count > 0)
        {
            total = startAt + issues.Count;
        }

        return new IssuePage(total, totalKnown, issues);
    }

    public async Task<JsonElement> LoadIssueFieldsAsync(string issueKey, IEnumerable<string> fields)
    {
        var fieldSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "summary" };
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                fieldSet.Add(field);
            }
        }

        var fieldsParam = string.Join(",", fieldSet);
        var primaryUrl = $"rest/api/{_settings.ApiVersion}/issue/{issueKey}?fields={Uri.EscapeDataString(fieldsParam)}&fieldsByKeys=true";
        var primaryFields = await TryLoadIssueFieldsAsync(primaryUrl, issueKey, "fields-filtered");
        if (primaryFields.ValueKind != JsonValueKind.Undefined)
        {
            return primaryFields;
        }

        var fallbackUrl = $"rest/api/{_settings.ApiVersion}/issue/{issueKey}";
        return await TryLoadIssueFieldsAsync(fallbackUrl, issueKey, "fallback");
    }

    public async Task<Dictionary<string, string>> LoadIssueFieldNamesAsync(string issueKey)
    {
        var url = $"rest/api/{_settings.ApiVersion}/issue/{issueKey}?expand=names&fields=summary";
        Console.WriteLine($"Jira Request: GET {new Uri(_httpClient.BaseAddress!, url)}");
        using var response = await _httpClient.GetAsync(url);
        var rawBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to load field names for {issueKey}: {(int)response.StatusCode} {rawBody}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var doc = JsonDocument.Parse(rawBody);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("names", out var namesElement) && namesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in namesElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    map[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }

        return map;
    }

    private async Task<JsonElement> TryLoadIssueFieldsAsync(string url, string issueKey, string label)
    {
        Console.WriteLine($"Jira Request: GET {new Uri(_httpClient.BaseAddress!, url)}");
        using var response = await _httpClient.GetAsync(url);
        var rawBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Failed to load issue {issueKey} ({label}): {(int)response.StatusCode} {rawBody}");
            return new JsonElement();
        }

        using var doc = JsonDocument.Parse(rawBody);
        if (doc.RootElement.TryGetProperty("fields", out var fieldsElement))
        {
            return fieldsElement.Clone();
        }

        var snippet = rawBody.Length > 1000 ? rawBody[..1000] + "..." : rawBody;
        Console.WriteLine($"Issue {issueKey} response missing fields ({label}). Body (truncated): {snippet}");
        return new JsonElement();
    }
}
