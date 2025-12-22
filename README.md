# JiraPriorityScore

Console app that loads issues from a Jira filter ID and logs key fields by request type.

## What it does
- Uses Jira API to page through all issues in a filter.
- For Request Type = Product PR, logs PriorityScore, Reach, Impact, Confidence, Effort.
- For Request Type = Engineering Enabler or Keep the Lights on (KTLO), logs PriorityScore, Business Weight, Time Criticality, Risk Reduction, Opportunity Enablement.
- Logs all processing steps to the console.

## Configure
Edit `appsettings.json` with your Jira site, credentials, filter ID, and custom field IDs.

## Run
```
dotnet run
```
