using System.Text.Json;
using JiraPriorityScore.Models;
using JiraPriorityScore.Utils;

namespace JiraPriorityScore.Services;

public class IssueProcessor
{
    private readonly JiraClient _jiraClient;
    private readonly JiraSettings _settings;

    public IssueProcessor(JiraClient jiraClient, JiraSettings settings)
    {
        _jiraClient = jiraClient;
        _settings = settings;
    }

    public async Task ProcessFilterAsync()
    {
        Console.WriteLine($"Using FilterId: {_settings.FilterId}");

        var fields = new[]
        {
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
        var startAt = 0;
        var total = 0;
        var totalKnown = false;
        var loggedTotal = false;

        while (true)
        {
            var page = await _jiraClient.LoadIssuesPageAsync(fields, startAt, maxResults);
            if (!loggedTotal)
            {
                total = page.Total;
                totalKnown = page.IsTotalKnown;
                if (totalKnown)
                {
                    Console.WriteLine($"Filter {_settings.FilterId} contains {total} issues.");
                }
                else
                {
                    Console.WriteLine($"Filter {_settings.FilterId} total is unknown; first page returned {page.Issues.Count} issues.");
                }
                loggedTotal = true;
            }

            if (page.Issues.Count == 0)
            {
                break;
            }

            foreach (var issue in page.Issues)
            {
                Console.WriteLine($"\nProcessing issue {issue.Key}...");
                var issueFields = issue.Fields;
                if (issueFields.ValueKind == JsonValueKind.Undefined)
                {
                    issueFields = await _jiraClient.LoadIssueFieldsAsync(issue.Key, fields);
                    if (issueFields.ValueKind == JsonValueKind.Undefined)
                    {
                        Console.WriteLine($"[{issue.Key}] Skipped: could not load fields.");
                        continue;
                    }
                }

                var requestType = await GetRequestTypeAsync(issue.Key, issueFields, fields);

                if (FieldParser.IsMatch(requestType, _settings.RequestTypeProductValue))
                {
                    await ProcessProductIssueAsync(issue.Key, issueFields);
                }
                else if (FieldParser.IsMatch(requestType, _settings.RequestTypeEngineeringEnablerValue) ||
                         FieldParser.IsMatch(requestType, _settings.RequestTypeKtloValue))
                {
                    await ProcessEngineeringIssueAsync(issue.Key, issueFields);
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

    private async Task<string?> GetRequestTypeAsync(string issueKey, JsonElement issueFields, string[] fields)
    {
        var requestType = FieldParser.GetFieldString(issueFields, _settings.RequestTypeFieldId);
        if (!string.IsNullOrWhiteSpace(requestType))
        {
            return requestType;
        }

        if (string.IsNullOrWhiteSpace(_settings.RequestTypeFieldName))
        {
            Console.WriteLine($"[{issueKey}] Request Type field not found. Check RequestTypeFieldId.");
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
                Console.WriteLine($"[{issueKey}] Request Type field name '{_settings.RequestTypeFieldName}' not found. Candidates: {string.Join(", ", candidates)}");
            }
            else
            {
                Console.WriteLine($"[{issueKey}] Request Type field name '{_settings.RequestTypeFieldName}' not found. No 'request' fields in names map.");
            }

            var fieldKeys = GetFieldKeys(issueFields);
            if (fieldKeys.Count > 0)
            {
                Console.WriteLine($"[{issueKey}] Fields present in issue: {string.Join(", ", fieldKeys)}");
            }

            return null;
        }

        if (!string.Equals(resolvedFieldId, _settings.RequestTypeFieldId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[{issueKey}] Resolved Request Type field id: {resolvedFieldId}");
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

        Console.WriteLine($"[{issueKey}] Product PR | PriorityScore={FieldParser.FormatNumber(priorityScore)} Reach={FieldParser.FormatNumber(reach)} Impact={FieldParser.FormatNumber(impact)} Confidence={FieldParser.FormatNumber(confidence)} Effort={FieldParser.FormatNumber(effort)}");

        var tempPriorityScore = 0d;
        var missingInputs = reach is null ||
                            impact is null ||
                            confidence is null ||
                            effort is null ||
                            effort == 0;

        if (!missingInputs)
        {
            tempPriorityScore = (reach.Value * impact.Value * confidence.Value) / effort.Value;
        }

        var roundedTemp = Math.Round(tempPriorityScore, 0, MidpointRounding.AwayFromZero);
        Console.WriteLine($"[{issueKey}] Product PR TempPriorityScore={roundedTemp:0}");
        var comment = $"[assignee] updated priority from {FieldParser.FormatNumber(priorityScore)} to {roundedTemp:0} " +
                      $"(Reach={FieldParser.FormatNumber(reach)}, Impact={FieldParser.FormatNumber(impact)}, Confidence={FieldParser.FormatNumber(confidence)}, Effort={FieldParser.FormatNumber(effort)})";

        await UpdatePriorityScoreIfNeededAsync(issueKey, priorityScore, roundedTemp, fields, comment, missingInputs || !priorityScore.HasValue);
    }

    private async Task ProcessEngineeringIssueAsync(string issueKey, JsonElement fields)
    {
        var priorityScore = FieldParser.GetFieldNumber(fields, _settings.PriorityScoreFieldId);
        var businessWeight = FieldParser.GetFieldNumber(fields, _settings.BusinessWeightFieldId);
        var timeCriticality = FieldParser.GetFieldNumber(fields, _settings.TimeCriticalityFieldId);
        var riskReduction = FieldParser.GetFieldNumber(fields, _settings.RiskReductionFieldId);
        var opportunityEnablement = FieldParser.GetFieldNumber(fields, _settings.OpportunityEnablementFieldId);

        Console.WriteLine($"[{issueKey}] Engineering Enabler/KTLO | PriorityScore={FieldParser.FormatNumber(priorityScore)} BusinessWeight={FieldParser.FormatNumber(businessWeight)} TimeCriticality={FieldParser.FormatNumber(timeCriticality)} RiskReduction={FieldParser.FormatNumber(riskReduction)} OpportunityEnablement={FieldParser.FormatNumber(opportunityEnablement)}");

        var tempPriorityScore = 0d;
        if (businessWeight is double bw &&
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
        Console.WriteLine($"[{issueKey}] Engineering Enabler/KTLO TempPriorityScore={roundedTemp:0}");
        var comment = $"[assignee] updated priority from {FieldParser.FormatNumber(priorityScore)} to {roundedTemp:0} " +
                      $"(Business Weight={FieldParser.FormatNumber(businessWeight)}, Time Criticality={FieldParser.FormatNumber(timeCriticality)}, Risk Reduction={FieldParser.FormatNumber(riskReduction)}, Opportunity Enablement={FieldParser.FormatNumber(opportunityEnablement)})";

        await UpdatePriorityScoreIfNeededAsync(issueKey, priorityScore, roundedTemp, fields, comment, false);
    }

    private async Task UpdatePriorityScoreIfNeededAsync(string issueKey, double? currentScore, double newScore, JsonElement fields, string commentText, bool forceUpdate)
    {
        var current = currentScore ?? 0d;
        if (!forceUpdate && Math.Abs(current - newScore) < 0.0001)
        {
            Console.WriteLine($"[{issueKey}] PriorityScore unchanged.");
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
            formattedComment = formattedComment.Replace("[assignee]", "Assignee");
        }


        if (_settings.DryRun)
        {
            Console.WriteLine($"[{issueKey}] DryRun - would update PriorityScore to {newScore:0.####}.");
            if (currentScore is null && newScore == 0)
            {
                Console.WriteLine($"[{issueKey}] DryRun - skipping comment because PriorityScore is null and new value is 0.");
            }
            else
            {
                Console.WriteLine($"[{issueKey}] DryRun - would add comment:\n{formattedComment}");
            }
            return;
        }

        var updated = await _jiraClient.UpdatePriorityScoreAsync(issueKey, newScore);
        if (updated)
        {
            Console.WriteLine($"[{issueKey}] PriorityScore updated to {newScore:0.####}.");
            if (currentScore is null && newScore == 0)
            {
                Console.WriteLine($"[{issueKey}] Skipped Jira comment because PriorityScore is null and new value is 0.");
                return;
            }

            var commented = await _jiraClient.AddCommentAsync(issueKey, commentText, assigneeAccountId);
            if (commented)
            {
                Console.WriteLine($"[{issueKey}] Comment added:\n{formattedComment}");
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
}
