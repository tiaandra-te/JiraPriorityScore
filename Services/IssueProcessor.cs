using System.Text.Json;
using JiraPriorityScore.Models;
using JiraPriorityScore.Utils;

namespace JiraPriorityScore.Services;

public class IssueProcessor
{
    private readonly JiraClient _jiraClient;
    private readonly JiraSettings _settings;
    private int _processedCount;
    private int _updatedCount;
    private int _commentCount;

    public IssueProcessor(JiraClient jiraClient, JiraSettings settings)
    {
        _jiraClient = jiraClient;
        _settings = settings;
    }

    public async Task ProcessFilterAsync()
    {
        _processedCount = 0;
        _updatedCount = 0;
        _commentCount = 0;

        Console.WriteLine($"Using FilterId: {_settings.FilterId}");

        var fields = new[]
        {
            "summary",
            "assignee",
            _settings.RequestTypeFieldId,
            _settings.PriorityScoreFieldId,
            _settings.ReachFieldId,
            _settings.ImpactFieldId,
            _settings.ConfidenceFieldId,
            _settings.EffortFieldId,
            _settings.BusinessWeightFieldId,
            _settings.TimeCriticalityFieldId,
            _settings.RiskReductionFieldId,
            _settings.OpportunityEnablementFieldId
        }.Where(f => !string.IsNullOrWhiteSpace(f))
         .Distinct(StringComparer.OrdinalIgnoreCase)
         .ToArray();

        var maxResults = _settings.PageSize > 0 ? _settings.PageSize : 50;
        var issueKeys = await _jiraClient.GetIssueKeysForFilterAsync(maxResults);
        Console.WriteLine($"Filter {_settings.FilterId} contains {issueKeys.Count} issues.");

        var seenIssueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issueKey in issueKeys)
        {
            if (!seenIssueKeys.Add(issueKey))
            {
                LogIssue(issueKey, "Skipped duplicate issue from paging.");
                continue;
            }

            var issueFields = await _jiraClient.LoadIssueFieldsAsync(issueKey, fields);
            if (issueFields.ValueKind == JsonValueKind.Undefined)
            {
                LogIssue(issueKey, "Skipped: could not load fields.");
                continue;
            }

            var summary = FieldParser.GetFieldString(issueFields, "summary") ?? "(no summary)";
            Console.WriteLine($"\nProcessing issue {issueKey} - {summary}...");
            _processedCount++;

            var requestType = await GetRequestTypeAsync(issueKey, issueFields, fields);

            if (FieldParser.IsMatch(requestType, _settings.RequestTypeProductValue))
            {
                await ProcessProductIssueAsync(issueKey, issueFields);
            }
            else if (FieldParser.IsMatch(requestType, _settings.RequestTypeEngineeringEnablerValue) ||
                     FieldParser.IsMatch(requestType, _settings.RequestTypeKtloValue))
            {
                await ProcessEngineeringIssueAsync(issueKey, issueFields);
            }
            else
            {
                var displayType = string.IsNullOrWhiteSpace(requestType) ? "(null)" : requestType;
                LogIssue(issueKey, $"Skipped: Request Type '{displayType}' not matched.");
            }
        }
    }

    private async Task<string?> GetRequestTypeAsync(string issueKey, JsonElement issueFields, string[] fields)
    {
        var requestType = FieldParser.GetFieldString(issueFields, _settings.RequestTypeFieldId);
        if (!string.IsNullOrWhiteSpace(requestType))
        {
            return requestType;
        }

        if (string.IsNullOrWhiteSpace(_settings.RequestTypeFieldName))
        {
            LogIssue(issueKey, "Request Type field not found. Check RequestTypeFieldId.");
            return null;
        }

        var names = await _jiraClient.LoadIssueFieldNamesAsync(issueKey);
        var resolvedFieldId = names.FirstOrDefault(pair =>
            string.Equals(pair.Value, _settings.RequestTypeFieldName, StringComparison.OrdinalIgnoreCase)).Key;

        if (string.IsNullOrWhiteSpace(resolvedFieldId))
        {
            var candidates = names
                .Where(pair => pair.Value.Contains("request", StringComparison.OrdinalIgnoreCase))
                .Select(pair => $"{pair.Key}='{pair.Value}'")
                .ToList();

            if (candidates.Count > 0)
            {
                LogIssue(issueKey, $"Request Type field name '{_settings.RequestTypeFieldName}' not found. Candidates: {string.Join(", ", candidates)}");
            }
            else
            {
                LogIssue(issueKey, $"Request Type field name '{_settings.RequestTypeFieldName}' not found. No 'request' fields in names map.");
            }

            return null;
        }

        if (!string.Equals(resolvedFieldId, _settings.RequestTypeFieldId, StringComparison.OrdinalIgnoreCase))
        {
            LogIssue(issueKey, $"Resolved Request Type field id: {resolvedFieldId}");
        }

        var fieldOnly = new[] { resolvedFieldId };
        var refreshed = await _jiraClient.LoadIssueFieldsAsync(issueKey, fieldOnly);
        return FieldParser.GetFieldString(refreshed, resolvedFieldId);
    }

    private async Task ProcessProductIssueAsync(string issueKey, JsonElement fields)
    {
        var priorityScore = FieldParser.GetFieldNumber(fields, _settings.PriorityScoreFieldId);
        var reach = FieldParser.GetFieldNumber(fields, _settings.ReachFieldId);
        var impact = FieldParser.GetFieldNumber(fields, _settings.ImpactFieldId);
        var confidence = FieldParser.GetFieldNumber(fields, _settings.ConfidenceFieldId);
        var effort = FieldParser.GetFieldNumber(fields, _settings.EffortFieldId);

        LogIssue(issueKey, $"Product PR | PriorityScore={FieldParser.FormatNumber(priorityScore)} Reach={FieldParser.FormatNumber(reach)} Impact={FieldParser.FormatNumber(impact)} Confidence={FieldParser.FormatNumber(confidence)} Effort={FieldParser.FormatNumber(effort)}");

        var tempPriorityScore = 0d;
        var allInputsNull = reach is null &&
                            impact is null &&
                            confidence is null &&
                            effort is null &&
                            priorityScore is null;
        var missingInputs = reach is null ||
                            impact is null ||
                            confidence is null ||
                            effort is null ||
                            effort == 0;

        if (!missingInputs &&
            reach is double r &&
            impact is double i &&
            confidence is double c &&
            effort is double e)
        {
            tempPriorityScore = (r * i * c) / e;
        }

        var roundedTemp = Math.Round(tempPriorityScore, 0, MidpointRounding.AwayFromZero);
        LogIssue(issueKey, $"Product PR TempPriorityScore={roundedTemp:0}");
        var comment = $"[assignee] updated Priority Score from {FieldParser.FormatNumber(priorityScore)} to {roundedTemp:0} " +
                      $"(Reach={FieldParser.FormatNumber(reach)}, Impact={FieldParser.FormatNumber(impact)}, Confidence={FieldParser.FormatNumber(confidence)}, Effort={FieldParser.FormatNumber(effort)})";

        await UpdatePriorityScoreIfNeededAsync(
            issueKey,
            priorityScore,
            roundedTemp,
            fields,
            comment,
            allInputsNull);
    }

    private async Task ProcessEngineeringIssueAsync(string issueKey, JsonElement fields)
    {
        var priorityScore = FieldParser.GetFieldNumber(fields, _settings.PriorityScoreFieldId);
        var businessWeight = FieldParser.GetFieldNumber(fields, _settings.BusinessWeightFieldId);
        var timeCriticality = FieldParser.GetFieldNumber(fields, _settings.TimeCriticalityFieldId);
        var riskReduction = FieldParser.GetFieldNumber(fields, _settings.RiskReductionFieldId);
        var opportunityEnablement = FieldParser.GetFieldNumber(fields, _settings.OpportunityEnablementFieldId);

        LogIssue(issueKey, $"Engineering Enabler/KTLO | PriorityScore={FieldParser.FormatNumber(priorityScore)} BusinessWeight={FieldParser.FormatNumber(businessWeight)} TimeCriticality={FieldParser.FormatNumber(timeCriticality)} RiskReduction={FieldParser.FormatNumber(riskReduction)} OpportunityEnablement={FieldParser.FormatNumber(opportunityEnablement)}");

        var tempPriorityScore = 0d;
        var missingInputs = businessWeight is null ||
                            timeCriticality is null ||
                            riskReduction is null ||
                            opportunityEnablement is null;

        if (!missingInputs &&
            businessWeight is double bw &&
            timeCriticality is double tc &&
            riskReduction is double rr &&
            opportunityEnablement is double oe)
        {
            tempPriorityScore = (((bw * 0.2) +
                                  (tc * 0.3) +
                                  (rr * 0.3) +
                                  (oe * 0.2) - 1) / 3) * 1000;
        }

        var roundedTemp = Math.Round(tempPriorityScore, 0, MidpointRounding.AwayFromZero);
        LogIssue(issueKey, $"Engineering Enabler/KTLO TempPriorityScore={roundedTemp:0}");
        var comment = $"[assignee] updated priority from {FieldParser.FormatNumber(priorityScore)} to {roundedTemp:0} " +
                      $"(Business Weight={FieldParser.FormatNumber(businessWeight)}, Time Criticality={FieldParser.FormatNumber(timeCriticality)}, Risk Reduction={FieldParser.FormatNumber(riskReduction)}, Opportunity Enablement={FieldParser.FormatNumber(opportunityEnablement)})";

        await UpdatePriorityScoreIfNeededAsync(
            issueKey,
            priorityScore,
            roundedTemp,
            fields,
            comment,
            missingInputs);
    }

    private async Task UpdatePriorityScoreIfNeededAsync(string issueKey, double? currentScore, double newScore, JsonElement fields, string commentText, bool skipComment)
    {
        var current = currentScore ?? 0d;
        if (Math.Abs(current - newScore) < 0.0001)
        {
            LogIssue(issueKey, "PriorityScore unchanged.");
            return;
        }

        var assigneeAccountId = GetAssigneeAccountId(fields);
        var assigneeDisplay = GetAssigneeDisplayName(fields);
        var formattedComment = commentText;
        if (!string.IsNullOrWhiteSpace(assigneeDisplay))
        {
            formattedComment = formattedComment.Replace("[assignee]", assigneeDisplay);
        }
        else
        {
            formattedComment = formattedComment.Replace("[assignee]", "").TrimStart();
        }


        if (_settings.DryRun)
        {
            LogIssue(issueKey, $"DryRun - would update PriorityScore to {newScore:0.####}.");
            if (skipComment || (currentScore is null && newScore == 0))
            {
                LogIssue(issueKey, "DryRun - skipping comment because PriorityScore and inputs are null.");
            }
            else
            {
                LogIssue(issueKey, $"DryRun - would add comment:\n\t{formattedComment}");
            }
            return;
        }

        var updated = await _jiraClient.UpdatePriorityScoreAsync(issueKey, newScore);
        if (updated)
        {
            LogIssue(issueKey, $"PriorityScore updated to {newScore:0.####}.");
            _updatedCount++;
            if (skipComment || (currentScore is null && newScore == 0))
            {
                LogIssue(issueKey, "Skipped Jira comment because PriorityScore and inputs are null.");
                return;
            }

            var commented = await _jiraClient.AddCommentAsync(issueKey, commentText, assigneeAccountId);
            if (commented)
            {
                LogIssue(issueKey, $"Comment added:\n\t{formattedComment}");
                _commentCount++;
            }
        }
    }

    private static string? GetAssigneeAccountId(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object ||
            !fields.TryGetProperty("assignee", out var assignee) ||
            assignee.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (assignee.TryGetProperty("accountId", out var accountId) && accountId.ValueKind == JsonValueKind.String)
        {
            return accountId.GetString();
        }

        return null;
    }

    private static string? GetAssigneeDisplayName(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object ||
            !fields.TryGetProperty("assignee", out var assignee) ||
            assignee.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (assignee.TryGetProperty("displayName", out var displayName) && displayName.ValueKind == JsonValueKind.String)
        {
            return displayName.GetString();
        }

        return null;
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

    private static void LogIssue(string issueKey, string message)
    {
        Console.WriteLine($"\t[{issueKey}] {message}");
    }

    public (int Processed, int Updated, int Commented) GetRunStats()
    {
        return (_processedCount, _updatedCount, _commentCount);
    }
}
