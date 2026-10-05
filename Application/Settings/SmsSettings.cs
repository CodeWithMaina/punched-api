namespace PunchedApi.Application.Settings;

public class SmsSettings
{
    public const string SectionName = "Sms";

    public string Provider { get; set; } = "africastalking";
    public string Username { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}
