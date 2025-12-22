using System.Net.Http.Headers;
using System.Text;
using JiraPriorityScore.Models;
using JiraPriorityScore.Services;
using JiraPriorityScore.Utils;
using Microsoft.Extensions.Configuration;

namespace JiraPriorityScore;

internal static class Program
{
    private static async Task Main()
    {
        try
        {
            var appSettingsPath = AppSettingsLocator.Find("appsettings.json");
            if (appSettingsPath == null)
            {
                Console.WriteLine("Could not find appsettings.json in the project root.");
                return;
            }

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(appSettingsPath) ?? Directory.GetCurrentDirectory())
                .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
                .Build();

            var settings = configuration.GetSection("Jira").Get<JiraSettings>();
            if (settings == null)
            {
                Console.WriteLine("Missing Jira settings.");
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.BaseUrl) ||
                string.IsNullOrWhiteSpace(settings.Email) ||
                string.IsNullOrWhiteSpace(settings.ApiToken) ||
                settings.FilterId <= 0)
            {
                Console.WriteLine("Jira BaseUrl, Email, ApiToken, and FilterId are required.");
                return;
            }

            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/")
            };

            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Email}:{settings.ApiToken}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var jiraClient = new JiraClient(httpClient, settings);
            var processor = new IssueProcessor(jiraClient, settings);

            await processor.ProcessFilterAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
