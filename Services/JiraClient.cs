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

    public async Task<List<string>> GetIssueKeysForFilterAsync(int maxResults)
    {
        var allKeys = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? nextPageToken = null;
        var isFirstPage = true;

        while (true)
        {
            var url = $"rest/api/{_settings.ApiVersion}/search/jql";
            var payload = new Dictionary<string, object?>
            {
                ["jql"] = $"filter={_settings.FilterId}",
                ["maxResults"] = maxResults,
                ["fields"] = new[] { "summary" }
            };

            if (isFirstPage)
            {
                // Use the base payload only.
            }
            else
            {
                payload["nextPageToken"] = nextPageToken;
            }

            var payloadJson = JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            LogRequest(HttpMethod.Post, new Uri(_httpClient.BaseAddress!, url), requestContent);
            LogRequestPayload(payloadJson);

            await DelayBeforeRequestAsync();
            using var response = await _httpClient.PostAsync(url, requestContent);
            var rawBody = await response.Content.ReadAsStringAsync();
            LogResponse(new Uri(_httpClient.BaseAddress!, url), response, rawBody);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to load filter {_settings.FilterId}: {(int)response.StatusCode} {rawBody}");
            }

            using var doc = JsonDocument.Parse(rawBody);
            var pageKeys = new List<string>();

            if (doc.RootElement.TryGetProperty("issues", out var issuesElement) && issuesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var issueElement in issuesElement.EnumerateArray())
                {
                    var key = issueElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        pageKeys.Add(key);
                    }
                }
            }

            if (pageKeys.Count == 0)
            {
                if (isFirstPage)
                {
                    var snippet = rawBody.Length > 1000 ? rawBody[..1000] + "..." : rawBody;
                    Console.WriteLine($"Filter response had zero issues. Body (truncated): {snippet}");
                }
                break;
            }

            var newKeys = 0;
            foreach (var key in pageKeys)
            {
                if (seenKeys.Add(key))
                {
                    allKeys.Add(key);
                    newKeys++;
                }
            }

            if (newKeys == 0)
            {
                Console.WriteLine("No new issues in page; stopping pagination to avoid repeat.");
                break;
            }

            if (doc.RootElement.TryGetProperty("nextPageToken", out var nextTokenProp) &&
                nextTokenProp.ValueKind == JsonValueKind.String)
            {
                nextPageToken = nextTokenProp.GetString();
            }
            else
            {
                nextPageToken = null;
            }

            if (string.IsNullOrWhiteSpace(nextPageToken))
            {
                break;
            }

            isFirstPage = false;
        }

        return allKeys;
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
        LogRequest(HttpMethod.Get, new Uri(_httpClient.BaseAddress!, url), null);
        await DelayBeforeRequestAsync();
        using var response = await _httpClient.GetAsync(url);
        var rawBody = await response.Content.ReadAsStringAsync();
        LogResponse(new Uri(_httpClient.BaseAddress!, url), response, rawBody);
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

    public async Task<bool> UpdatePriorityScoreAsync(string issueKey, double newScore)
    {
        var url = $"rest/api/{_settings.ApiVersion}/issue/{issueKey}";
        var payload = new
        {
            fields = new Dictionary<string, object>
            {
                [_settings.PriorityScoreFieldId] = newScore
            }
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var requestContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        LogRequest(HttpMethod.Put, new Uri(_httpClient.BaseAddress!, url), requestContent);
        LogRequestPayload(payloadJson);

        await DelayBeforeRequestAsync();
        using var response = await _httpClient.PutAsync(
            url,
            requestContent);

        if (response.IsSuccessStatusCode)
        {
            if (_settings.LogResponses)
            {
                var successBody = await response.Content.ReadAsStringAsync();
                LogResponse(new Uri(_httpClient.BaseAddress!, url), response, successBody);
            }
            return true;
        }

        var body = await response.Content.ReadAsStringAsync();
        LogResponse(new Uri(_httpClient.BaseAddress!, url), response, body);
        Console.WriteLine($"Failed to update PriorityScore for {issueKey}: {(int)response.StatusCode} {body}");
        return false;
    }

    public async Task<bool> AddCommentAsync(string issueKey, string commentText, string? assigneeAccountId)
    {
        var url = $"rest/api/{_settings.ApiVersion}/issue/{issueKey}/comment";
        var content = new List<object>
        {
            new
            {
                type = "paragraph",
                content = BuildCommentContent(commentText, assigneeAccountId)
            }
        };

        var payload = new
        {
            body = new
            {
                type = "doc",
                version = 1,
                content
            }
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var requestContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        LogRequest(HttpMethod.Post, new Uri(_httpClient.BaseAddress!, url), requestContent);
        LogRequestPayload(payloadJson);

        await DelayBeforeRequestAsync();
        using var response = await _httpClient.PostAsync(
            url,
            requestContent);

        if (response.IsSuccessStatusCode)
        {
            if (_settings.LogResponses)
            {
                var successBody = await response.Content.ReadAsStringAsync();
                LogResponse(new Uri(_httpClient.BaseAddress!, url), response, successBody);
            }
            return true;
        }

        var body = await response.Content.ReadAsStringAsync();
        LogResponse(new Uri(_httpClient.BaseAddress!, url), response, body);
        Console.WriteLine($"Failed to add comment for {issueKey}: {(int)response.StatusCode} {body}");
        return false;
    }

    private static object[] BuildCommentContent(string commentText, string? assigneeAccountId)
    {
        var parts = commentText.Split(new[] { "[assignee]" }, StringSplitOptions.None);
        var content = new List<object>();

        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(assigneeAccountId))
        {
            if (!string.IsNullOrWhiteSpace(parts[0]))
            {
                content.Add(new { type = "text", text = parts[0] });
            }

            content.Add(new { type = "mention", attrs = new { id = assigneeAccountId } });

            if (!string.IsNullOrWhiteSpace(parts[1]))
            {
                content.Add(new { type = "text", text = parts[1] });
            }
        }
        else
        {
            var sanitized = commentText.Replace("[assignee]", "").TrimStart();
            content.Add(new { type = "text", text = sanitized });
        }

        return content.ToArray();
    }

    private async Task<JsonElement> TryLoadIssueFieldsAsync(string url, string issueKey, string label)
    {
        LogRequest(HttpMethod.Get, new Uri(_httpClient.BaseAddress!, url), null);
        await DelayBeforeRequestAsync();
        using var response = await _httpClient.GetAsync(url);
        var rawBody = await response.Content.ReadAsStringAsync();
        LogResponse(new Uri(_httpClient.BaseAddress!, url), response, rawBody);
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

    private void LogRequest(HttpMethod method, Uri url, HttpContent? content)
    {
        if (!_settings.LogRequests)
        {
            return;
        }

        Console.WriteLine($"Jira Request: {method} {url}");
        if (!_settings.LogRequestHeaders)
        {
            return;
        }

        foreach (var header in _httpClient.DefaultRequestHeaders)
        {
            var value = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? "[redacted]"
                : string.Join(", ", header.Value);
            Console.WriteLine($"Header: {header.Key}={value}");
        }

        if (content != null)
        {
            foreach (var header in content.Headers)
            {
                Console.WriteLine($"Header: {header.Key}={string.Join(", ", header.Value)}");
            }
        }
    }

    private void LogResponse(Uri url, HttpResponseMessage response, string? body)
    {
        if (!_settings.LogResponses)
        {
            return;
        }

        Console.WriteLine($"Jira Response: {(int)response.StatusCode} {response.ReasonPhrase} {url}");
        if (string.IsNullOrWhiteSpace(body))
        {
            Console.WriteLine("Jira Response Body: (empty)");
            return;
        }
        Console.WriteLine($"Jira Response Body: {body}");
    }

    private void LogRequestPayload(string? payloadJson)
    {
        if (!_settings.LogRequests)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            Console.WriteLine("Jira Request Payload: (empty)");
            return;
        }

        Console.WriteLine($"Jira Request Payload: {payloadJson}");
    }

    private async Task DelayBeforeRequestAsync()
    {
        if (_settings.RequestDelayMs <= 0)
        {
            return;
        }

        if (_settings.LogRequests)
        {
            Console.WriteLine($"Jira Request Delay: {_settings.RequestDelayMs}ms");
        }

        await Task.Delay(_settings.RequestDelayMs);
    }
}
