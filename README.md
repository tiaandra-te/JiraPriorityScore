# JiraPriorityScore

Console app that loads issues from a Jira filter ID, computes PriorityScore values, and updates Jira (unless DryRun is enabled). It can also email a run report.

## What it does
- Pages through issues in the configured Jira filter ID.
- Product PR: computes `(Reach * Impact * Confidence) / Effort`, rounds to integer, updates PriorityScore when changed.
- Engineering Enabler/KTLO: computes `((Business Weight*0.2 + Time Criticality*0.3 + Risk Reduction*0.3 + Opportunity Enablement*0.2 - 1) / 3) * 1000`, rounds to integer, updates PriorityScore when changed.
- Adds a Jira comment on updates (unless updating from null to 0).
- Logs actions to the console and can send the log as an email report.

## Configure
Edit `appsettings.json`:
- `Jira` section: site URL, email, API token, filter ID, custom field IDs, request type values, `DryRun`.
- `Email` section: `SendReport`, SendGrid `ApiKey`, `FromEmail`, `ToEmail`, and `Subject`.

Notes:
- Set `DryRun` to `true` to avoid Jira writes while testing.
- Set `SendReport` to `false` to skip email.

## Build
```
dotnet build
```

## Run
```
dotnet run
```
