using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoWildfireApi.Models;

[Table("ingestion_log")]
public class IngestionLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("source")]
    [MaxLength(50)]
    public string Source { get; set; } = string.Empty;

    [Column("dataset_key")]
    [MaxLength(255)]
    public string DatasetKey { get; set; } = string.Empty;

    [Column("records_loaded")]
    public int RecordsLoaded { get; set; }

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "pending"; // pending, success, failed

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
}
