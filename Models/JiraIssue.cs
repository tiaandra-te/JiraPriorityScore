using System.Text.Json;

namespace JiraPriorityScore.Models;

public record JiraIssue(string Key, JsonElement Fields);
