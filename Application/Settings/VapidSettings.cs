namespace PunchedApi.Application.Settings;

public class VapidSettings
{
    public const string SectionName = "Vapid";

    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = "mailto:hello@punched.app";
    public bool Enabled { get; set; }
}
