using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

internal class Program
{
    private static async Task Main(string[] args)
    {
        try
        {
            var appSettingsPath = FindAppSettingsPath("appsettings.json");
            if (appSettingsPath == null)
            {
                Console.WriteLine("Could not find appsettings.json in the project root.");
                return;
            }

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(appSettingsPath) ?? Directory.GetCurrentDirectory())
                .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
                .Build();

            var settings = configuration.GetSection("Jira").Get<JiraSettings>();
            if (settings == null)
            {
                Console.WriteLine("Missing Jira settings.");
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.BaseUrl) ||
                string.IsNullOrWhiteSpace(settings.Email) ||
                string.IsNullOrWhiteSpace(settings.ApiToken) ||
                settings.FilterId <= 0)
            {
                Console.WriteLine("Jira BaseUrl, Email, ApiToken, and FilterId are required.");
                return;
            }

            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/")
            };

            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Email}:{settings.ApiToken}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            Console.WriteLine($"Using FilterId: {settings.FilterId}");

            var fields = new[]
            {
                settings.RequestTypeFieldId,
                settings.PriorityScoreFieldId,
                settings.ReachFieldId,
                settings.ImpactFieldId,
                settings.ConfidenceFieldId,
                settings.EffortFieldId,
                settings.BusinessWeightFieldId,
                settings.TimeCriticalityFieldId,
                settings.RiskReductionFieldId,
                settings.OpportunityEnablementFieldId
            }.Where(f => !string.IsNullOrWhiteSpace(f))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray();

            var maxResults = settings.PageSize > 0 ? settings.PageSize : 50;
            var startAt = 0;
            var total = 0;
            var totalKnown = false;
            var loggedTotal = false;

            while (true)
            {
                var page = await LoadIssuesPageAsync(httpClient, settings, fields, startAt, maxResults);
                if (!loggedTotal)
                {
                    total = page.Total;
                    totalKnown = page.IsTotalKnown;
                    if (totalKnown)
                    {
                        Console.WriteLine($"Filter {settings.FilterId} contains {total} issues.");
                    }
                    else
                    {
                        Console.WriteLine($"Filter {settings.FilterId} total is unknown; first page returned {page.Issues.Count} issues.");
                    }
                    loggedTotal = true;
                }

                if (page.Issues.Count == 0)
                {
                    break;
                }

                foreach (var issue in page.Issues)
                {
                    Console.WriteLine($"Processing issue {issue.Key}...");
                    var issueFields = issue.Fields;
                    if (issueFields.ValueKind == JsonValueKind.Undefined)
                    {
                        issueFields = await LoadIssueFieldsAsync(httpClient, settings, issue.Key, fields);
                        if (issueFields.ValueKind == JsonValueKind.Undefined)
                        {
                            Console.WriteLine($"[{issue.Key}] Skipped: could not load fields.");
                            continue;
                        }
                    }

                    var requestType = await GetRequestTypeAsync(httpClient, settings, issue.Key, issueFields, fields);

                    if (IsRequestTypeMatch(requestType, settings.RequestTypeProductValue))
                    {
                        LogProductIssue(issue.Key, issueFields, settings);
                    }
                    else if (IsRequestTypeMatch(requestType, settings.RequestTypeEngineeringEnablerValue) ||
                             IsRequestTypeMatch(requestType, settings.RequestTypeKtloValue))
                    {
                        LogEngineeringIssue(issue.Key, issueFields, settings);
                    }
                    else
                    {
                        var displayType = string.IsNullOrWhiteSpace(requestType) ? "(null)" : requestType;
                        Console.WriteLine($"[{issue.Key}] Skipped: Request Type '{displayType}' not matched.");
                    }
                }

                startAt += page.Issues.Count;
                if (totalKnown)
                {
                    if (startAt >= total)
                    {
                        break;
                    }
                }
                else if (page.Issues.Count < maxResults)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static async Task<IssuePage> LoadIssuesPageAsync(HttpClient httpClient, JiraSettings settings, string[] fields, int startAt, int maxResults)
    {
        var jql = $"filter={settings.FilterId}";
        var fieldsParam = fields.Length == 0 ? "summary" : string.Join(",", fields);
        var url = $"rest/api/{settings.ApiVersion}/search/jql?jql={Uri.EscapeDataString(jql)}&startAt={startAt}&maxResults={maxResults}&fields={Uri.EscapeDataString(fieldsParam)}";
        Console.WriteLine($"Jira Request: GET {new Uri(httpClient.BaseAddress!, url)}");

        using var response = await httpClient.GetAsync(url);
        var rawBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Failed to load filter {settings.FilterId}: {(int)response.StatusCode} {rawBody}");
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

    private static async Task<JsonElement> LoadIssueFieldsAsync(HttpClient httpClient, JiraSettings settings, string issueKey, string[] fields)
    {
        var fieldSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        fieldSet.Add("summary");
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                fieldSet.Add(field);
            }
        }

        var fieldsParam = string.Join(",", fieldSet);
        var primaryUrl = $"rest/api/{settings.ApiVersion}/issue/{issueKey}?fields={Uri.EscapeDataString(fieldsParam)}&fieldsByKeys=true";
        var primaryFields = await TryLoadIssueFieldsAsync(httpClient, primaryUrl, issueKey, "fields-filtered");
        if (primaryFields.ValueKind != JsonValueKind.Undefined)
        {
            return primaryFields;
        }

        var fallbackUrl = $"rest/api/{settings.ApiVersion}/issue/{issueKey}";
        var fallbackFields = await TryLoadIssueFieldsAsync(httpClient, fallbackUrl, issueKey, "fallback");
        return fallbackFields;
    }

    private static async Task<Dictionary<string, string>> LoadIssueFieldNamesAsync(HttpClient httpClient, JiraSettings settings, string issueKey)
    {
        var url = $"rest/api/{settings.ApiVersion}/issue/{issueKey}?expand=names&fields=summary";
        Console.WriteLine($"Jira Request: GET {new Uri(httpClient.BaseAddress!, url)}");
        using var response = await httpClient.GetAsync(url);
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

    private static async Task<string?> GetRequestTypeAsync(HttpClient httpClient, JiraSettings settings, string issueKey, JsonElement issueFields, string[] fields)
    {
        var requestType = GetFieldString(issueFields, settings.RequestTypeFieldId);
        if (!string.IsNullOrWhiteSpace(requestType))
        {
            return requestType;
        }

        if (string.IsNullOrWhiteSpace(settings.RequestTypeFieldName))
        {
            Console.WriteLine($"[{issueKey}] Request Type field not found. Check RequestTypeFieldId.");
            return null;
        }

        var names = await LoadIssueFieldNamesAsync(httpClient, settings, issueKey);
        var resolvedFieldId = names.FirstOrDefault(pair =>
            string.Equals(pair.Value, settings.RequestTypeFieldName, StringComparison.OrdinalIgnoreCase)).Key;

        if (string.IsNullOrWhiteSpace(resolvedFieldId))
        {
            var candidates = names
                .Where(pair => pair.Value.Contains("request", StringComparison.OrdinalIgnoreCase))
                .Select(pair => $"{pair.Key}='{pair.Value}'")
                .ToList();

            if (candidates.Count > 0)
            {
                Console.WriteLine($"[{issueKey}] Request Type field name '{settings.RequestTypeFieldName}' not found. Candidates: {string.Join(", ", candidates)}");
            }
            else
            {
                Console.WriteLine($"[{issueKey}] Request Type field name '{settings.RequestTypeFieldName}' not found. No 'request' fields in names map.");
            }

            var fieldKeys = GetFieldKeys(issueFields);
            if (fieldKeys.Count > 0)
            {
                Console.WriteLine($"[{issueKey}] Fields present in issue: {string.Join(", ", fieldKeys)}");
            }
            return null;
        }

        if (!string.Equals(resolvedFieldId, settings.RequestTypeFieldId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[{issueKey}] Resolved Request Type field id: {resolvedFieldId}");
        }

        var fieldOnly = new[] { resolvedFieldId };
        var refreshed = await LoadIssueFieldsAsync(httpClient, settings, issueKey, fieldOnly);
        return GetFieldString(refreshed, resolvedFieldId);
    }

    private static async Task<JsonElement> TryLoadIssueFieldsAsync(HttpClient httpClient, string url, string issueKey, string label)
    {
        Console.WriteLine($"Jira Request: GET {new Uri(httpClient.BaseAddress!, url)}");
        using var response = await httpClient.GetAsync(url);
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

    private static string? FindAppSettingsPath(string fileName)
    {
        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        }.Where(path => !string.IsNullOrWhiteSpace(path))
         .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in searchRoots)
        {
            var directory = new DirectoryInfo(root);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static void LogProductIssue(string issueKey, JsonElement fields, JiraSettings settings)
    {
        var priorityScore = GetFieldNumber(fields, settings.PriorityScoreFieldId);
        var reach = GetFieldNumber(fields, settings.ReachFieldId);
        var impact = GetFieldNumber(fields, settings.ImpactFieldId);
        var confidence = GetFieldNumber(fields, settings.ConfidenceFieldId);
        var effort = GetFieldNumber(fields, settings.EffortFieldId);

        Console.WriteLine($"[{issueKey}] Product PR | PriorityScore={FormatNumber(priorityScore)} Reach={FormatNumber(reach)} Impact={FormatNumber(impact)} Confidence={FormatNumber(confidence)} Effort={FormatNumber(effort)}");
    }

    private static void LogEngineeringIssue(string issueKey, JsonElement fields, JiraSettings settings)
    {
        var priorityScore = GetFieldNumber(fields, settings.PriorityScoreFieldId);
        var businessWeight = GetFieldNumber(fields, settings.BusinessWeightFieldId);
        var timeCriticality = GetFieldNumber(fields, settings.TimeCriticalityFieldId);
        var riskReduction = GetFieldNumber(fields, settings.RiskReductionFieldId);
        var opportunityEnablement = GetFieldNumber(fields, settings.OpportunityEnablementFieldId);

        Console.WriteLine($"[{issueKey}] Engineering Enabler/KTLO | PriorityScore={FormatNumber(priorityScore)} BusinessWeight={FormatNumber(businessWeight)} TimeCriticality={FormatNumber(timeCriticality)} RiskReduction={FormatNumber(riskReduction)} OpportunityEnablement={FormatNumber(opportunityEnablement)}");
    }

    private static string? GetFieldString(JsonElement fields, string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || !fields.TryGetProperty(fieldId, out var fieldValue))
        {
            return null;
        }

        if (fieldValue.ValueKind == JsonValueKind.String)
        {
            return fieldValue.GetString();
        }

        if (fieldValue.ValueKind == JsonValueKind.Object)
        {
            if (fieldValue.TryGetProperty("name", out var nameProp))
            {
                return nameProp.GetString();
            }

            if (fieldValue.TryGetProperty("value", out var valueProp))
            {
                return valueProp.GetString();
            }
        }

        return fieldValue.ToString();
    }

    private static double? GetFieldNumber(JsonElement fields, string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || !fields.TryGetProperty(fieldId, out var fieldValue))
        {
            return null;
        }

        if (fieldValue.ValueKind == JsonValueKind.Number && fieldValue.TryGetDouble(out var number))
        {
            return number;
        }

        if (fieldValue.ValueKind == JsonValueKind.String && double.TryParse(fieldValue.GetString(), out var parsed))
        {
            return parsed;
        }

        if (fieldValue.ValueKind == JsonValueKind.Object)
        {
            if (fieldValue.TryGetProperty("value", out var valueProp) &&
                valueProp.ValueKind == JsonValueKind.String &&
                double.TryParse(valueProp.GetString(), out var valueParsed))
            {
                return valueParsed;
            }

            if (fieldValue.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String &&
                double.TryParse(nameProp.GetString(), out var nameParsed))
            {
                return nameParsed;
            }
        }

        if (fieldValue.ValueKind == JsonValueKind.Array)
        {
            var first = fieldValue.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Number && first.TryGetDouble(out var firstNumber))
            {
                return firstNumber;
            }

            if (first.ValueKind == JsonValueKind.String && double.TryParse(first.GetString(), out var firstParsed))
            {
                return firstParsed;
            }
        }

        return null;
    }

    private static string FormatNumber(double? value)
    {
        return value.HasValue ? value.Value.ToString("0.####") : "null";
    }

    private static bool IsRequestTypeMatch(string? requestType, string? expected)
    {
        if (string.IsNullOrWhiteSpace(requestType) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(requestType.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> GetFieldKeys(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object)
        {
            return new List<string>();
        }

        return fields.EnumerateObject()
            .Select(prop => prop.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private record JiraSettings
    {
        public string BaseUrl { get; init; } = "";
        public string ApiVersion { get; init; } = "3";
        public string Email { get; init; } = "";
        public string ApiToken { get; init; } = "";
        public int FilterId { get; init; }
        public int PageSize { get; init; } = 50;

        public string RequestTypeFieldId { get; init; } = "";
        public string RequestTypeFieldName { get; init; } = "Request Type";
        public string PriorityScoreFieldId { get; init; } = "";
        public string ReachFieldId { get; init; } = "";
        public string ImpactFieldId { get; init; } = "";
        public string ConfidenceFieldId { get; init; } = "";
        public string EffortFieldId { get; init; } = "";
        public string BusinessWeightFieldId { get; init; } = "";
        public string TimeCriticalityFieldId { get; init; } = "";
        public string RiskReductionFieldId { get; init; } = "";
        public string OpportunityEnablementFieldId { get; init; } = "";

        public string RequestTypeProductValue { get; init; } = "Product PR";
        public string RequestTypeEngineeringEnablerValue { get; init; } = "Engineering Enabler";
        public string RequestTypeKtloValue { get; init; } = "Keep the Lights on (KTLO)";
    }

    private record JiraIssue(string Key, JsonElement Fields);

    private record IssuePage(int Total, bool IsTotalKnown, List<JiraIssue> Issues);
}
