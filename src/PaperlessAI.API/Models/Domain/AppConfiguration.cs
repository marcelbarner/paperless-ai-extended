namespace PaperlessAI.API.Models.Domain;

public class AppConfiguration
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
