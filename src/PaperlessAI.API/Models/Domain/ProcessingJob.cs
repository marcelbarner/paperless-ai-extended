namespace PaperlessAI.API.Models.Domain;

public class ProcessingJob
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public string DocumentTitle { get; set; } = string.Empty;
    public JobType JobType { get; set; }
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
}

// Both bleibt für Rückwärtskompatibilität mit bestehenden DB-Einträgen
public enum JobType { Ocr, Ai, Both }
public enum JobStatus { Pending, Processing, Done, Failed }
