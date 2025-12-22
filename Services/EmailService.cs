using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JiraPriorityScore.Models;

namespace JiraPriorityScore.Services;

public class EmailService
{
    private readonly HttpClient _httpClient;
    private readonly EmailSettings _settings;

    public EmailService(HttpClient httpClient, EmailSettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    public async Task SendAsync(string? subject, string body)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) ||
            string.IsNullOrWhiteSpace(_settings.FromEmail) ||
            string.IsNullOrWhiteSpace(_settings.ToEmail))
        {
            Console.WriteLine("Email settings are missing. Skipping report email.");
            return;
        }

        var finalSubject = string.IsNullOrWhiteSpace(subject) ? _settings.Subject : subject;
        var payload = new
        {
            personalizations = new[]
            {
                new
                {
                    to = new[]
                    {
                        new { email = _settings.ToEmail }
                    }
                }
            },
            from = new { email = _settings.FromEmail },
            subject = finalSubject,
            content = new[]
            {
                new { type = "text/plain", value = body }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Failed to send report email: {(int)response.StatusCode} {errorBody}");
        }
        else
        {
            Console.WriteLine("Report email sent.");
        }
    }
}
