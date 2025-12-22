namespace JiraPriorityScore.Services;

public class EmailService
{
    public Task SendAsync(string subject, string body)
    {
        Console.WriteLine($"EmailService placeholder: {subject}");
        return Task.CompletedTask;
    }
}
