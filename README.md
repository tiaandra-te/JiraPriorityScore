# JiraPriorityScore

Console app that loads issues from a Jira filter ID, computes PriorityScore values, and updates Jira (unless DryRun is enabled). It can also email a run report.

## What it does
- Pages through issues in the configured Jira filter ID.
- Product PR: computes `(Reach * Impact * Confidence) / Effort`, rounds to integer, updates PriorityScore when changed.
- Engineering Enabler/KTLO: computes `((Business Weight*0.2 + Time Criticality*0.3 + Risk Reduction*0.3 + Opportunity Enablement*0.2 - 1) / 3) * 1000`, rounds to integer, updates PriorityScore when changed.
- Adds a Jira comment on updates (unless updating from null to 0).
- Logs actions to the console and can send the log as an email report.
 - Optional request logging and delay to throttle Jira API calls.

## Configure
Edit `appsettings.json`:
- `Jira` section: site URL, email, API token, filter ID, custom field IDs, request type values, `DryRun`.
- Logging options: `LogRequests`, `LogRequestHeaders`, `LogResponses`.
- Throttling option: `RequestDelayMs` (milliseconds to wait before each Jira API call).
- `Email` section: `SendReport`, SendGrid `ApiKey`, `FromEmail`, `ToEmail`, and `Subject`.

### Jira settings
- `BaseUrl`: Jira site base URL (must include trailing slash).
- `Email`: Jira account email for API authentication.
- `ApiVersion`: Jira REST API version (default `3`).
- `ApiToken`: Jira API token for the account email.
- `FilterId`: Jira filter ID to load issues from.
- `PageSize`: Max issues per page when querying the filter.
- `DryRun`: When `true`, no Jira updates/comments are sent.
- `LogRequests`: When `true`, logs Jira request method + URL (and payloads).
- `LogRequestHeaders`: When `true`, logs request headers (Authorization redacted).
- `LogResponses`: When `true`, logs full Jira response bodies.
- `RequestDelayMs`: Delay in milliseconds before each Jira API call.
- `RequestTypeFieldId`: Custom field ID that stores Request Type (e.g., `customfield_12345`).
- `RequestTypeFieldName`: Friendly field name used if the ID lookup fails.
- `PriorityScoreFieldId`: Custom field ID for PriorityScore.
- `ReachFieldId`: Custom field ID for Reach.
- `ImpactFieldId`: Custom field ID for Impact.
- `ConfidenceFieldId`: Custom field ID for Confidence.
- `EffortFieldId`: Custom field ID for Effort.
- `BusinessWeightFieldId`: Custom field ID for Business Weight.
- `TimeCriticalityFieldId`: Custom field ID for Time Criticality.
- `RiskReductionFieldId`: Custom field ID for Risk Reduction.
- `OpportunityEnablementFieldId`: Custom field ID for Opportunity Enablement.
- `RequestTypeProductValue`: Request Type value that maps to Product PR logic.
- `RequestTypeEngineeringEnablerValue`: Request Type value for Engineering Enabler logic.
- `RequestTypeKtloValue`: Request Type value for KTLO logic.

### Email settings
- `Provider`: Email provider name (currently `SendGrid`).
- `SendReport`: When `true`, sends a run report email.
- `ApiKey`: SendGrid API key.
- `FromEmail`: From address for the report email.
- `ToEmail`: Recipient for the report email.
- `Subject`: Email subject line.

Notes:
- Set `DryRun` to `true` to avoid Jira writes while testing.
- Set `SendReport` to `false` to skip email.
- Set `RequestDelayMs` > 0 to slow down Jira calls.

## Build
```
dotnet build
```

## Run
```
dotnet run
```
