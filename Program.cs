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
        var logBuffer = new StringBuilder();
        var teeWriter = new TeeTextWriter(Console.Out, logBuffer);
        Console.SetOut(teeWriter);
        Console.SetError(teeWriter);

        EmailSettings? emailSettings = null;

        var startedAt = DateTimeOffset.UtcNow;

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
            emailSettings = configuration.GetSection("Email").Get<EmailSettings>();
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
                BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(60)
            };

            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Email}:{settings.ApiToken}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var jiraClient = new JiraClient(httpClient, settings);
            var processor = new IssueProcessor(jiraClient, settings);

            await processor.ProcessFilterAsync();

            var (processed, updated, commented) = processor.GetRunStats();
            var duration = DateTimeOffset.UtcNow - startedAt;
            Console.WriteLine();
            Console.WriteLine("Run report");
            Console.WriteLine($"Duration: {duration:hh\\:mm\\:ss}");
            Console.WriteLine($"Issues processed: {processed}");
            Console.WriteLine($"Issues updated: {updated}");
            Console.WriteLine($"Comments submitted: {commented}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            if (emailSettings != null && emailSettings.SendReport)
            {
                using var emailClient = new HttpClient();
                var emailService = new EmailService(emailClient, emailSettings);
                await emailService.SendAsync(emailSettings.Subject, logBuffer.ToString());
            }
        }
    }
}
