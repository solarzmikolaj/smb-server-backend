using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models;

[Table("AuditLogs")]
public class AuditLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int? UserId { get; set; }

    [Required]
    [MaxLength(50)]
    [Column(TypeName = "varchar(50)")]
    public string Action { get; set; } = string.Empty; // Login, Logout, FileUpload, FileDelete, etc.

    [MaxLength(100)]
    [Column(TypeName = "varchar(100)")]
    public string? Resource { get; set; } // Nazwa zasobu (np. nazwa pliku)

    [MaxLength(1000)]
    [Column(TypeName = "varchar(1000)")]
    public string? Details { get; set; } // Dodatkowe szczegóły

    [Required]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [MaxLength(45)]
    [Column(TypeName = "varchar(45)")]
    public string? IpAddress { get; set; }

    [MaxLength(500)]
    [Column(TypeName = "varchar(500)")]
    public string? UserAgent { get; set; }

    [Required]
    [MaxLength(20)]
    [Column(TypeName = "varchar(20)")]
    public AuditLogSeverity Severity { get; set; } = AuditLogSeverity.Info;

    // Navigation property
    [ForeignKey("UserId")]
    public virtual User? User { get; set; }
}

public enum AuditLogSeverity
{
    Info,
    Warning,
    Error
}


