namespace JiraPriorityScore.Models;

public record IssuePage(int Total, bool IsTotalKnown, List<JiraIssue> Issues);
