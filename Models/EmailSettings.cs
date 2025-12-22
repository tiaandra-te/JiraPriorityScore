namespace JiraPriorityScore.Models;

public record EmailSettings
{
    public string Provider { get; init; } = "SendGrid";
    public string ApiKey { get; init; } = "";
    public string FromEmail { get; init; } = "";
    public string ToEmail { get; init; } = "";
    public string Subject { get; init; } = "JiraPriorityScore report";
    public bool SendReport { get; init; } = true;
}
