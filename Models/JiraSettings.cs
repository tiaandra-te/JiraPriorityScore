namespace JiraPriorityScore.Models;

public record JiraSettings
{
    public string BaseUrl { get; init; } = "";
    public string ApiVersion { get; init; } = "3";
    public string Email { get; init; } = "";
    public string ApiToken { get; init; } = "";
    public int FilterId { get; init; }
    public int PageSize { get; init; } = 50;
    public bool DryRun { get; init; } = true;

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
