using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models;

[Table("TrashItems")]
public class TrashItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    [Required]
    [MaxLength(500)]
    [Column(TypeName = "varchar(500)")]
    public string OriginalPath { get; set; } = string.Empty; // Oryginalna ścieżka pliku/folderu

    [Required]
    [MaxLength(500)]
    [Column(TypeName = "varchar(500)")]
    public string TrashPath { get; set; } = string.Empty; // Ścieżka w koszu

    [Required]
    [MaxLength(255)]
    [Column(TypeName = "varchar(255)")]
    public string Name { get; set; } = string.Empty; // Nazwa pliku/folderu

    [Required]
    [MaxLength(20)]
    [Column(TypeName = "varchar(20)")]
    public string Type { get; set; } = string.Empty; // "file" lub "folder"

    public long Size { get; set; } // Rozmiar w bajtach (dla folderów: suma plików)

    [Required]
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow; // Data usunięcia

    public DateTime? ExpiresAt { get; set; } // Data automatycznego usunięcia (np. po 30 dniach)

    // Navigation property
    [ForeignKey("UserId")]
    public virtual User User { get; set; } = null!;
}



