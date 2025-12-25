using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models;

[Table("Users")]
public class User
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column(TypeName = "varchar(100)")]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [EmailAddress]
    [Column(TypeName = "varchar(255)")]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    [Column(TypeName = "varchar(256)")]
    public string PasswordHash { get; set; } = string.Empty;

    [Required]
    [Column(TypeName = "varchar(20)")]
    public UserRole Role { get; set; } = UserRole.User;

    [Required]
    public bool IsApproved { get; set; } = false;

    [Required]
    public bool IsActive { get; set; } = true;

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ApprovedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    [Required]
    [MaxLength(500)]
    [Column(TypeName = "varchar(500)")]
    public string SmbFolderPath { get; set; } = string.Empty;

    // 2FA fields
    [MaxLength(100)]
    [Column(TypeName = "varchar(100)")]
    public string? TwoFactorSecret { get; set; }

    [Required]
    public bool TwoFactorEnabled { get; set; } = false;

    // Quota fields (w bajtach, null = brak limitu)
    public long? StorageQuota { get; set; } // Limit przestrzeni dyskowej w bajtach

    // Navigation properties
    public virtual ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
    public virtual ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
}

public enum UserRole
{
    User,
    Admin
}


