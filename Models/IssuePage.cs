namespace JiraPriorityScore.Models;

public record IssuePage(int Total, bool IsTotalKnown, bool IsLast, List<JiraIssue> Issues);
