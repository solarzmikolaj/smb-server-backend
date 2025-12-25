using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly SmbService _smbService;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(ApplicationDbContext context, SmbService smbService, ILogger<DashboardController> logger)
    {
        _context = context;
        _smbService = smbService;
        _logger = logger;
    }

    /// <summary>
    /// Pobiera statystyki użytkownika
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<UserStatsResponse>> GetUserStats([FromHeader] string? authorization)
    {
        var userId = await GetUserIdFromTokenAsync(authorization);
        if (userId == null)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Użytkownik nie został znaleziony.");
        }

        try
        {
            // Oblicz rozmiar folderu użytkownika
            long totalSize = 0;
            int fileCount = 0;
            int folderCount = 0;

            try
            {
                var allFiles = _smbService.GetAllFilesRecursive(user.SmbFolderPath).ToList();
                totalSize = allFiles.Sum(f => f.Item1.Length);
                fileCount = allFiles.Count;

                var allDirs = _smbService.GetAllDirectoriesRecursive(user.SmbFolderPath).ToList();
                folderCount = allDirs.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error calculating folder size for user {UserId}", userId);
            }

            // Pobierz quota
            var quota = user.StorageQuota;
            var quotaUsed = totalSize;
            var quotaPercent = quota.HasValue && quota.Value > 0 
                ? (double)quotaUsed / quota.Value * 100 
                : 0;

            return Ok(new UserStatsResponse
            {
                TotalSize = totalSize,
                FileCount = fileCount,
                FolderCount = folderCount,
                Quota = quota,
                QuotaUsed = quotaUsed,
                QuotaPercent = quotaPercent,
                QuotaRemaining = quota.HasValue ? Math.Max(0, quota.Value - quotaUsed) : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user stats");
            return StatusCode(500, "Błąd podczas pobierania statystyk.");
        }
    }

    /// <summary>
    /// Pobiera top 10 największych plików użytkownika
    /// </summary>
    [HttpGet("largest-files")]
    public async Task<ActionResult<IEnumerable<DashboardFileInfoDto>>> GetLargestFiles([FromHeader] string? authorization, [FromQuery] int limit = 10)
    {
        var userId = await GetUserIdFromTokenAsync(authorization);
        if (userId == null)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Użytkownik nie został znaleziony.");
        }

        try
        {
            var allFiles = _smbService.GetAllFilesRecursive(user.SmbFolderPath)
                .OrderByDescending(f => f.Item1.Length)
                .Take(limit)
                .Select(f => new DashboardFileInfoDto
                {
                    Name = f.Item1.Name,
                    Path = f.Item2,
                    Size = f.Item1.Length,
                    LastModified = f.Item1.LastWriteTime,
                    Extension = f.Item1.Extension
                })
                .ToList();

            return Ok(allFiles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting largest files");
            return StatusCode(500, "Błąd podczas pobierania największych plików.");
        }
    }

    /// <summary>
    /// Pobiera ostatnio używane pliki
    /// </summary>
    [HttpGet("recent-files")]
    public async Task<ActionResult<IEnumerable<DashboardFileInfoDto>>> GetRecentFiles([FromHeader] string? authorization, [FromQuery] int limit = 10)
    {
        var userId = await GetUserIdFromTokenAsync(authorization);
        if (userId == null)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Użytkownik nie został znaleziony.");
        }

        try
        {
            // Pobierz ostatnie aktywności związane z plikami
            var recentActivities = await _context.AuditLogs
                .Where(a => a.UserId == userId && 
                           (a.Action == "FileDownload" || a.Action == "FilePreview" || a.Action == "FileUpload") &&
                           !string.IsNullOrEmpty(a.Resource))
                .OrderByDescending(a => a.Timestamp)
                .Take(limit * 2) // Weź więcej, bo mogą być duplikaty
                .Select(a => a.Resource)
                .Distinct()
                .Take(limit)
                .ToListAsync();

            var files = new List<DashboardFileInfoDto>();

            foreach (var resource in recentActivities)
            {
                try
                {
                    if (string.IsNullOrEmpty(resource)) continue;
                    var filePath = Path.Combine(user.SmbFolderPath, resource);
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Exists)
                    {
                        files.Add(new DashboardFileInfoDto
                        {
                            Name = fileInfo.Name,
                            Path = resource,
                            Size = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            Extension = fileInfo.Extension
                        });
                    }
                }
                catch
                {
                    // Ignoruj błędy dla pojedynczych plików
                }
            }

            return Ok(files);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent files");
            return StatusCode(500, "Błąd podczas pobierania ostatnio używanych plików.");
        }
    }

    /// <summary>
    /// Pobiera historię aktywności użytkownika
    /// </summary>
    [HttpGet("activity-history")]
    public async Task<ActionResult<DashboardPagedResult<ActivityLogDto>>> GetActivityHistory(
        [FromHeader] string? authorization,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? actionFilter = null)
    {
        var userId = await GetUserIdFromTokenAsync(authorization);
        if (userId == null)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        try
        {
            IQueryable<Models.AuditLog> query = _context.AuditLogs
                .Where(a => a.UserId == userId);

            if (!string.IsNullOrEmpty(actionFilter))
            {
                query = query.Where(a => a.Action.Contains(actionFilter));
            }

            var totalCount = await query.CountAsync();
            var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

            var activities = await query
                .OrderByDescending(a => a.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(a => new ActivityLogDto
                {
                    Id = a.Id,
                    Action = a.Action,
                    Resource = a.Resource,
                    Details = a.Details,
                    Timestamp = a.Timestamp,
                    Severity = a.Severity.ToString()
                })
                .ToListAsync();

            return Ok(new DashboardPagedResult<ActivityLogDto>
            {
                Items = activities,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                TotalPages = totalPages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activity history");
            return StatusCode(500, "Błąd podczas pobierania historii aktywności.");
        }
    }

    /// <summary>
    /// Pobiera użycie przestrzeni w czasie (dla wykresów)
    /// </summary>
    [HttpGet("space-usage")]
    public async Task<ActionResult<IEnumerable<SpaceUsagePoint>>> GetSpaceUsage(
        [FromHeader] string? authorization,
        [FromQuery] int days = 30)
    {
        var userId = await GetUserIdFromTokenAsync(authorization);
        if (userId == null)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Użytkownik nie został znaleziony.");
        }

        try
        {
            // Pobierz logi uploadów z ostatnich dni
            var startDate = DateTime.UtcNow.AddDays(-days);
            var uploadLogs = await _context.AuditLogs
                .Where(a => a.UserId == userId && 
                           a.Action == "FileUpload" &&
                           a.Timestamp >= startDate)
                .OrderBy(a => a.Timestamp)
                .ToListAsync();

            // Oblicz aktualny rozmiar
            long currentSize = 0;
            try
            {
                var allFiles = _smbService.GetAllFilesRecursive(user.SmbFolderPath).ToList();
                currentSize = allFiles.Sum(f => f.Item1.Length);
            }
            catch
            {
                // Ignoruj błędy
            }

            // Utwórz punkty danych (dziennie)
            var points = new List<SpaceUsagePoint>();
            var date = startDate.Date;
            var cumulativeSize = 0L;

            while (date <= DateTime.UtcNow.Date)
            {
                var dayUploads = uploadLogs
                    .Where(a => a.Timestamp.Date == date)
                    .ToList();

                // Szacuj rozmiar na podstawie logów (uproszczone)
                // W rzeczywistości lepiej byłoby przechowywać rozmiar w logach
                cumulativeSize += dayUploads.Count * 1024 * 1024; // Szacunek: 1MB na upload

                points.Add(new SpaceUsagePoint
                {
                    Date = date,
                    Size = Math.Min(cumulativeSize, currentSize) // Nie przekraczaj aktualnego rozmiaru
                });

                date = date.AddDays(1);
            }

            return Ok(points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting space usage");
            return StatusCode(500, "Błąd podczas pobierania użycia przestrzeni.");
        }
    }

    /// <summary>
    /// Pobiera statystyki systemu (tylko dla admina)
    /// </summary>
    [HttpGet("system-stats")]
    public async Task<ActionResult<SystemStatsResponse>> GetSystemStats([FromHeader] string? authorization)
    {
        var userId = await GetUserIdFromTokenAsync(authorization);
        if (userId == null)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.Role != UserRole.Admin)
        {
            return Forbid("Tylko administrator może przeglądać statystyki systemu.");
        }

        try
        {
            var totalUsers = await _context.Users.CountAsync(u => u.IsActive);
            var activeUsers = await _context.Users.CountAsync(u => u.IsActive && u.LastLoginAt.HasValue && u.LastLoginAt.Value >= DateTime.UtcNow.AddDays(30));
            var pendingUsers = await _context.Users.CountAsync(u => !u.IsApproved && u.IsActive);
            
            var totalStorage = 0L;
            var usersWithQuota = await _context.Users
                .Where(u => u.StorageQuota.HasValue)
                .ToListAsync();

            // Oblicz całkowite użycie - rozmiar folderu users/ i wszystkich podfolderów
            try
            {
                var allFiles = _smbService.GetAllFilesRecursive("users").ToList();
                totalStorage = allFiles.Sum(f => f.Item1.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nie można obliczyć całkowitego użycia przestrzeni dla folderu users/");
                // Fallback: oblicz dla wszystkich użytkowników
                var allUsers = await _context.Users
                    .Where(u => u.IsActive && !string.IsNullOrEmpty(u.SmbFolderPath))
                    .ToListAsync();
                
                foreach (var u in allUsers)
                {
                    try
                    {
                        var files = _smbService.GetAllFilesRecursive(u.SmbFolderPath).ToList();
                        totalStorage += files.Sum(f => f.Item1.Length);
                    }
                    catch
                    {
                        // Ignoruj błędy dla pojedynczych użytkowników
                    }
                }
            }

            return Ok(new SystemStatsResponse
            {
                TotalUsers = totalUsers,
                ActiveUsers = activeUsers,
                PendingUsers = pendingUsers,
                TotalStorage = totalStorage,
                UsersWithQuota = usersWithQuota.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system stats");
            return StatusCode(500, "Błąd podczas pobierania statystyk systemu.");
        }
    }

    private async Task<int?> GetUserIdFromTokenAsync(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
            return null;

        var token = authorization.Replace("Bearer ", "");
        var parts = token.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int userId))
            return null;

        var user = await _context.Users.FindAsync(userId);
        return user?.IsActive == true ? userId : null;
    }
}

public class UserStatsResponse
{
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public long? Quota { get; set; }
    public long QuotaUsed { get; set; }
    public double QuotaPercent { get; set; }
    public long? QuotaRemaining { get; set; }
}

// FileInfoDto i PagedResult są zdefiniowane w FileController.cs

public class DashboardFileInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Extension { get; set; } = string.Empty;
}

public class ActivityLogDto
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Resource { get; set; }
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; }
    public string Severity { get; set; } = string.Empty;
}

public class SpaceUsagePoint
{
    public DateTime Date { get; set; }
    public long Size { get; set; }
}

public class SystemStatsResponse
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int PendingUsers { get; set; }
    public long TotalStorage { get; set; }
    public int UsersWithQuota { get; set; }
}

public class DashboardPagedResult<T>
{
    public IEnumerable<T> Items { get; set; } = new List<T>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

