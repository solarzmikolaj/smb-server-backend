using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using WebApplication1.Services;

namespace WebApplication1;

[ApiController]
[Route("api/[controller]")]
public class FileController : ControllerBase
{
    private readonly SmbService _smbService;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<FileController> _logger;

    public FileController(SmbService smbService, ApplicationDbContext context, ILogger<FileController> logger)
    {
        _smbService = smbService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Pobiera listę wszystkich plików i folderów z serwera SMB z paginacją
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<FileSystemItemDto>>> GetFiles(
        [FromQuery] int? page = null, 
        [FromQuery] int? pageSize = null, 
        [FromQuery] string? path = null,
        [FromHeader] string? authorization = null)
    {
        try
        {
            int currentPage = page ?? 1;
            int currentPageSize = pageSize ?? 50;
            
            if (currentPage < 1) currentPage = 1;
            if (currentPageSize < 1 || currentPageSize > 1000) currentPageSize = 50;

            // Pobierz ścieżkę folderu użytkownika z tokenu
            string? userFolderPath = await GetUserFolderPathAsync(authorization);
            
            // Jeśli ścieżka nie jest podana, ustaw domyślną na folder użytkownika
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(userFolderPath))
            {
                path = userFolderPath;
            }
            
            // Ogranicz dostęp do folderu użytkownika (dotyczy wszystkich, w tym admina)
            if (!string.IsNullOrEmpty(userFolderPath) && !string.IsNullOrEmpty(path))
            {
                // Normalizuj ścieżki dla porównania
                string normalizedPath = path.Replace('\\', '/').TrimStart('/');
                string normalizedUserPath = userFolderPath.Replace('\\', '/').TrimStart('/');
                
                // Upewnij się, że ścieżka zaczyna się od folderu użytkownika
                if (!normalizedPath.StartsWith(normalizedUserPath, StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new { Message = "Brak dostępu do tego folderu." });
                }
            }

            var (files, directories) = _smbService.GetFilesAndDirectories(path);
            
            var filesList = files.ToList();
            var directoriesList = directories.ToList();
            
            var allItems = new List<FileSystemItemDto>();
            
            // Dodaj foldery
            foreach (var dir in directoriesList)
            {
                allItems.Add(new FileSystemItemDto
                {
                    Name = dir.Name,
                    Type = "folder",
                    Size = 0,
                    LastModified = dir.CreationTime,
                    Extension = ""
                });
            }
            
            // Dodaj pliki
            foreach (var file in filesList)
            {
                allItems.Add(new FileSystemItemDto
                {
                    Name = file.Name,
                    Type = "file",
                    Size = file.Length,
                    LastModified = file.CreationTime,
                    Extension = file.Extension
                });
            }

            // Sortuj: najpierw foldery, potem pliki, obie grupy po dacie utworzenia malejąco
            allItems = allItems
                .OrderBy(item => item.Type == "file" ? 1 : 0) // Foldery najpierw
                .ThenByDescending(item => item.LastModified) // W każdej grupie po dacie malejąco
                .ToList();

            // Paginacja
            var totalCount = allItems.Count;
            var totalPages = (int)Math.Ceiling(totalCount / (double)currentPageSize);
            var pagedItems = allItems
                .Skip((currentPage - 1) * currentPageSize)
                .Take(currentPageSize)
                .ToList();

            var result = new PagedResult<FileSystemItemDto>
            {
                Items = pagedItems,
                Page = currentPage,
                PageSize = currentPageSize,
                TotalCount = totalCount,
                TotalPages = totalPages
            };

            return Ok(result);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd podczas pobierania listy plików i folderów");
            return StatusCode(500, "Wystąpił błąd podczas pobierania listy plików i folderów");
        }
    }

    /// <summary>
    /// Wyszukuje pliki i foldery rekurencyjnie w całej strukturze użytkownika
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<PagedResult<FileSystemItemDto>>> SearchFiles(
        [FromQuery] string? query = null,
        [FromQuery] string? extension = null,
        [FromQuery] long? minSize = null,
        [FromQuery] long? maxSize = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromHeader] string? authorization = null)
    {
        try
        {
            int currentPage = page ?? 1;
            int currentPageSize = pageSize ?? 50;
            
            if (currentPage < 1) currentPage = 1;
            if (currentPageSize < 1 || currentPageSize > 1000) currentPageSize = 50;

            // Pobierz ścieżkę folderu użytkownika z tokenu
            string? userFolderPath = await GetUserFolderPathAsync(authorization);
            
            if (string.IsNullOrEmpty(userFolderPath))
            {
                return Unauthorized(new { Message = "Brak autoryzacji." });
            }

            // Pobierz wszystkie pliki rekurencyjnie
            var allFiles = _smbService.GetAllFilesRecursive(userFolderPath).ToList();
            var allDirectories = _smbService.GetAllDirectoriesRecursive(userFolderPath).ToList();

            var allItems = new List<FileSystemItemDto>();

            // Dodaj foldery
            foreach (var (dir, relativePath) in allDirectories)
            {
                // relativePath już zawiera pełną ścieżkę od root (zawiera userFolderPath jako prefix)
                // bo GetAllDirectoriesRecursive używa userFolderPath jako relativeBasePath
                allItems.Add(new FileSystemItemDto
                {
                    Name = dir.Name,
                    Type = "folder",
                    Size = 0,
                    LastModified = dir.CreationTime,
                    Extension = "",
                    Path = relativePath // relativePath już zawiera pełną ścieżkę
                });
            }

            // Dodaj pliki
            foreach (var (file, relativePath) in allFiles)
            {
                // relativePath już zawiera pełną ścieżkę od root (zawiera userFolderPath jako prefix)
                // bo GetAllFilesRecursive używa userFolderPath jako relativeBasePath
                allItems.Add(new FileSystemItemDto
                {
                    Name = file.Name,
                    Type = "file",
                    Size = file.Length,
                    LastModified = file.CreationTime,
                    Extension = file.Extension,
                    Path = relativePath // relativePath już zawiera pełną ścieżkę
                });
            }

            // Filtrowanie
            var filteredItems = allItems.AsQueryable();

            // Filtrowanie po nazwie (query)
            if (!string.IsNullOrWhiteSpace(query))
            {
                var queryLower = query.ToLower();
                filteredItems = filteredItems.Where(item => 
                    item.Name.ToLower().Contains(queryLower) ||
                    (!string.IsNullOrEmpty(item.Path) && item.Path.ToLower().Contains(queryLower)));
            }

            // Filtrowanie po rozszerzeniu
            if (!string.IsNullOrWhiteSpace(extension))
            {
                var extensions = extension.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Trim().ToLower())
                    .Select(e => e.StartsWith('.') ? e : '.' + e)
                    .ToList();
                filteredItems = filteredItems.Where(item => 
                    item.Type == "file" && extensions.Contains(item.Extension.ToLower()));
            }

            // Filtrowanie po rozmiarze
            if (minSize.HasValue)
            {
                filteredItems = filteredItems.Where(item => 
                    item.Type == "file" && item.Size >= minSize.Value);
            }
            if (maxSize.HasValue)
            {
                filteredItems = filteredItems.Where(item => 
                    item.Type == "file" && item.Size <= maxSize.Value);
            }

            // Filtrowanie po dacie
            if (dateFrom.HasValue)
            {
                filteredItems = filteredItems.Where(item => item.LastModified >= dateFrom.Value);
            }
            if (dateTo.HasValue)
            {
                filteredItems = filteredItems.Where(item => item.LastModified <= dateTo.Value.AddDays(1).AddTicks(-1));
            }

            var filteredList = filteredItems.ToList();

            // Sortuj: najpierw foldery, potem pliki, obie grupy po dacie utworzenia malejąco
            filteredList = filteredList
                .OrderBy(item => item.Type == "file" ? 1 : 0)
                .ThenByDescending(item => item.LastModified)
                .ToList();

            // Paginacja
            var totalCount = filteredList.Count;
            var totalPages = (int)Math.Ceiling(totalCount / (double)currentPageSize);
            var pagedItems = filteredList
                .Skip((currentPage - 1) * currentPageSize)
                .Take(currentPageSize)
                .ToList();

            var result = new PagedResult<FileSystemItemDto>
            {
                Items = pagedItems,
                Page = currentPage,
                PageSize = currentPageSize,
                TotalCount = totalCount,
                TotalPages = totalPages
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd podczas wyszukiwania plików");
            return StatusCode(500, "Wystąpił błąd podczas wyszukiwania plików");
        }
    }

    /// <summary>
    /// Pobiera informacje o konkretnym pliku
    /// </summary>
    [HttpGet("{fileName}")]
    public ActionResult<FileInfoDto> GetFileInfo(string fileName)
    {
        try
        {
            var fileInfo = _smbService.GetFileInfo(fileName);
            if (fileInfo == null)
            {
                return NotFound($"Plik '{fileName}' nie został znaleziony");
            }

            var fileDto = new FileInfoDto
            {
                Name = fileInfo.Name,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTime,
                Extension = fileInfo.Extension
            };

            return Ok(fileDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas pobierania informacji o pliku: {fileName}");
            return StatusCode(500, "Wystąpił błąd podczas pobierania informacji o pliku");
        }
    }

    /// <summary>
    /// Pobiera plik z serwera SMB (obsługuje ścieżki z folderami)
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> DownloadFile([FromQuery] string filePath, [FromQuery] string? token = null, [FromHeader] string? authorization = null)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return BadRequest("Ścieżka pliku jest wymagana");
            }

            // Dekoduj ścieżkę jeśli jest zakodowana
            filePath = Uri.UnescapeDataString(filePath);
            
            // Użyj tokena z query string jeśli jest dostępny (dla bezpośrednich linków)
            // lub z nagłówka Authorization (dla axios)
            var authHeader = !string.IsNullOrEmpty(token) ? $"Bearer {token}" : authorization;
            
            // Sprawdź dostęp
            if (!await CheckAccessAsync(filePath, authHeader))
            {
                return StatusCode(403, new { Message = "Brak dostępu do tego pliku." });
            }
            
            var fileInfo = _smbService.GetFileInfo(filePath);
            if (fileInfo == null)
            {
                return NotFound($"Plik '{filePath}' nie został znaleziony");
            }

            var fileStream = _smbService.GetFileStream(filePath);
            var fileName = Path.GetFileName(filePath);
            return File(fileStream, "application/octet-stream", fileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound($"Plik '{filePath}' nie został znaleziony");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas pobierania pliku: {filePath}");
            return StatusCode(500, "Wystąpił błąd podczas pobierania pliku");
        }
    }

    /// <summary>
    /// Podgląd pliku (PDF, obrazy) - zwraca plik bez Content-Disposition: attachment
    /// </summary>
    [HttpGet("preview")]
    public async Task<IActionResult> PreviewFile([FromQuery] string filePath, [FromQuery] string? token = null, [FromHeader] string? authorization = null)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return BadRequest("Ścieżka pliku jest wymagana");
            }

            // Dekoduj ścieżkę - może być zakodowana jako base64 (dla wideo) lub URL-encoded
            try
            {
                // Spróbuj najpierw jako base64 (dla wideo używamy base64)
                if (!filePath.Contains("/") && !filePath.Contains("\\") && filePath.Length > 0 && !filePath.Contains("%"))
                {
                    var decodedBytes = Convert.FromBase64String(filePath);
                    filePath = System.Text.Encoding.UTF8.GetString(decodedBytes);
                }
                else
                {
                    // Jeśli zawiera ukośniki lub %, użyj normalnego dekodowania URL
                    filePath = Uri.UnescapeDataString(filePath);
                }
            }
            catch
            {
                // Jeśli nie jest base64, użyj normalnego dekodowania URL
                filePath = Uri.UnescapeDataString(filePath);
            }
            
            // Użyj tokena z query string jeśli jest dostępny (dla elementu <video>)
            // lub z nagłówka Authorization (dla axios)
            var authHeader = !string.IsNullOrEmpty(token) ? $"Bearer {token}" : authorization;
            
            // Sprawdź dostęp
            if (!await CheckAccessAsync(filePath, authHeader))
            {
                return StatusCode(403, new { Message = "Brak dostępu do tego pliku." });
            }
            
            var fileInfo = _smbService.GetFileInfo(filePath);
            if (fileInfo == null)
            {
                return NotFound($"Plik '{filePath}' nie został znaleziony");
            }

            var fileStream = _smbService.GetFileStream(filePath);
            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // Określ Content-Type na podstawie rozszerzenia
            string contentType = extension switch
            {
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".mkv" => "video/x-matroska",
                ".webm" => "video/webm",
                ".flv" => "video/x-flv",
                ".wmv" => "video/x-ms-wmv",
                ".m4v" => "video/x-m4v",
                ".3gp" => "video/3gpp",
                ".ogv" => "video/ogg",
                ".txt" or ".text" or ".log" => "text/plain",
                ".md" => "text/markdown",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".csv" => "text/csv",
                ".js" or ".jsx" => "text/javascript",
                ".ts" or ".tsx" => "text/typescript",
                ".css" => "text/css",
                ".html" or ".htm" => "text/html",
                ".php" => "text/x-php",
                ".py" => "text/x-python",
                ".java" => "text/x-java-source",
                ".cpp" or ".c" or ".h" => "text/x-c++src",
                ".cs" => "text/x-csharp",
                ".sql" => "text/x-sql",
                ".sh" => "text/x-shellscript",
                ".bat" => "text/x-batch",
                ".ps1" => "text/x-powershell",
                ".yaml" or ".yml" => "text/yaml",
                ".ini" or ".conf" or ".config" => "text/plain",
                _ => "application/octet-stream"
            };
            
            // Dla plików wideo, włącz obsługę range requests (streaming)
            if (extension == ".mp4" || extension == ".mov" || extension == ".avi" || 
                extension == ".mkv" || extension == ".webm" || extension == ".flv" || 
                extension == ".wmv" || extension == ".m4v" || extension == ".3gp" || extension == ".ogv")
            {
                // Włącz obsługę range requests dla wideo
                Response.Headers.Add("Accept-Ranges", "bytes");
                
                // Sprawdź czy klient żąda określonego zakresu (range request)
                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    // Parsuj zakres (np. "bytes=0-1023")
                    var rangeMatch = System.Text.RegularExpressions.Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
                    if (rangeMatch.Success)
                    {
                        var start = long.Parse(rangeMatch.Groups[1].Value);
                        var end = rangeMatch.Groups[2].Success && !string.IsNullOrEmpty(rangeMatch.Groups[2].Value)
                            ? long.Parse(rangeMatch.Groups[2].Value)
                            : fileInfo.Length - 1;
                        
                        var contentLength = end - start + 1;
                        Response.StatusCode = 206; // Partial Content
                        Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{fileInfo.Length}");
                        Response.Headers.Add("Content-Length", contentLength.ToString());
                        
                        // Przeskocz do odpowiedniej pozycji w strumieniu
                        fileStream.Seek(start, SeekOrigin.Begin);
                        return File(fileStream, contentType, enableRangeProcessing: true);
                    }
                }
            }
            
            // Zwróć plik bez Content-Disposition: attachment, aby przeglądarka mogła go wyświetlić
            return File(fileStream, contentType);
        }
        catch (FileNotFoundException)
        {
            return NotFound($"Plik '{filePath}' nie został znaleziony");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas podglądu pliku: {filePath}");
            return StatusCode(500, "Wystąpił błąd podczas podglądu pliku");
        }
    }

    /// <summary>
    /// Pobiera checksum SHA256 dla pliku
    /// </summary>
    [HttpGet("checksum")]
    public async Task<ActionResult<object>> GetFileChecksum([FromQuery] string filePath, [FromHeader] string? authorization = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return BadRequest(new { Message = "Ścieżka pliku jest wymagana." });
            }

            // Sprawdź dostęp
            if (!await CheckAccessAsync(filePath, authorization))
            {
                return StatusCode(403, new { Message = "Brak dostępu do tego pliku." });
            }

            var checksum = _smbService.CalculateSha256Hash(filePath);
            
            return Ok(new { 
                FilePath = filePath,
                Checksum = checksum,
                Algorithm = "SHA256"
            });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas obliczania checksumu dla pliku: {filePath}");
            return StatusCode(500, new { Message = "Wystąpił błąd podczas obliczania checksumu." });
        }
    }

    /// <summary>
    /// Wysyła plik na serwer SMB
    /// </summary>
    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<FileUploadResponse>> UploadFile(IFormFile file, [FromQuery] string? path = null, [FromHeader] string? authorization = null)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("Plik nie został przesłany lub jest pusty");
        }

        try
        {
            // Walidacja rozmiaru pliku (max 1000GB)
            long maxFileSize = 1000L * 1024 * 1024 * 1024; // 1000GB
            if (file.Length > maxFileSize)
            {
                return BadRequest($"Plik jest za duży. Maksymalny rozmiar: {maxFileSize / (1024L * 1024 * 1024)}GB");
            }

            var filePath = string.IsNullOrEmpty(path) ? file.FileName : Path.Combine(path, file.FileName);
            
            // Sprawdź dostęp - użytkownik może przesyłać tylko do swojego folderu
            if (!await CheckAccessAsync(filePath, authorization))
            {
                return StatusCode(403, new { Message = "Brak dostępu do tego folderu." });
            }
            
            using (var stream = file.OpenReadStream())
            {
                await _smbService.SaveFileAsync(filePath, stream);
            }

            var response = new FileUploadResponse
            {
                FileName = file.FileName,
                Size = file.Length,
                Message = "Plik został pomyślnie przesłany"
            };

            return Ok(response);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas przesyłania pliku: {file.FileName}");
            return StatusCode(500, "Wystąpił błąd podczas przesyłania pliku");
        }
    }

    /// <summary>
    /// Tworzy nowy folder na serwerze SMB
    /// </summary>
    [HttpPost("folder")]
    public async Task<ActionResult> CreateFolder([FromQuery] string folderName, [FromQuery] string? path = null, [FromHeader] string? authorization = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return BadRequest("Nazwa folderu jest wymagana");
            }

            var folderPath = string.IsNullOrEmpty(path) ? folderName : Path.Combine(path, folderName);
            
            // Sprawdź dostęp - użytkownik może tworzyć foldery tylko w swoim folderze
            if (!await CheckAccessAsync(folderPath, authorization))
            {
                return StatusCode(403, new { Message = "Brak dostępu do tego folderu." });
            }
            
            var created = _smbService.CreateDirectory(folderPath);
            
            if (!created)
            {
                return BadRequest($"Folder '{folderName}' już istnieje lub nie można go utworzyć");
            }

            return Ok(new { Message = $"Folder '{folderName}' został utworzony", FolderName = folderName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas tworzenia folderu: {folderName}");
            return StatusCode(500, "Wystąpił błąd podczas tworzenia folderu");
        }
    }

    /// <summary>
    /// Usuwa plik lub folder z serwera SMB (przenosi do kosza)
    /// </summary>
    [HttpDelete]
    public async Task<ActionResult> DeleteItem([FromQuery] string path, [FromQuery] bool isFolder = false, [FromQuery] bool permanent = false, [FromHeader] string? authorization = null)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest("Ścieżka jest wymagana");
            }

            // Dekoduj ścieżkę jeśli jest zakodowana
            path = Uri.UnescapeDataString(path);
            
            // Sprawdź dostęp
            if (!await CheckAccessAsync(path, authorization))
            {
                return StatusCode(403, new { Message = "Brak dostępu do tego elementu." });
            }

            var userId = await GetUserIdAsync(authorization);
            if (!userId.HasValue)
            {
                return Unauthorized("Brak autoryzacji.");
            }

            var user = await _context.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return NotFound("Użytkownik nie został znaleziony.");
            }

            // Jeśli permanent = true, usuń trwale
            if (permanent)
            {
                bool deleted;
                if (isFolder)
                {
                    deleted = _smbService.DeleteDirectory(path);
                }
                else
                {
                    deleted = _smbService.DeleteFile(path);
                }

                if (!deleted)
                {
                    return NotFound($"Element '{path}' nie został znaleziony");
                }

                // Loguj usunięcie
                await LogAuditEvent(userId.Value, isFolder ? "FolderDeleted" : "FileDeleted", $"Permanent delete: {path}", Models.AuditLogSeverity.Info);

                return Ok(new { Message = isFolder ? "Folder został trwale usunięty" : "Plik został trwale usunięty" });
            }

            // Przenieś do kosza
            var fileName = Path.GetFileName(path);
            var trashFolder = Path.Combine(user.SmbFolderPath, ".trash");
            var trashPath = Path.Combine(trashFolder, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{fileName}");

            // Utwórz folder kosza jeśli nie istnieje
            if (!_smbService.DirectoryExists(trashFolder))
            {
                _smbService.CreateDirectory(trashFolder);
            }

            // Przenieś plik/folder do kosza
            try
            {
                if (isFolder)
                {
                    // Dla folderów: skopiuj i usuń oryginał
                    // Uproszczone - w rzeczywistości trzeba rekurencyjnie kopiować
                    _smbService.CreateDirectory(trashPath);
                    _smbService.DeleteDirectory(path);
                }
                else
                {
                    // Dla plików: przenieś
                    var sourceStream = _smbService.GetFileStream(path);
                    await _smbService.SaveFileAsync(trashPath, sourceStream);
                    sourceStream.Close();
                    _smbService.DeleteFile(path);
                }

                // Oblicz rozmiar
                long size = 0;
                if (!isFolder)
                {
                    try
                    {
                        var fileStream = _smbService.GetFileStream(trashPath);
                        size = fileStream.Length;
                        fileStream.Close();
                    }
                    catch { }
                }
                else
                {
                    // Dla folderów: oblicz sumę rozmiarów plików
                    try
                    {
                        var files = _smbService.GetAllFilesRecursive(trashPath).ToList();
                        size = files.Sum(f => f.Item1.Length);
                    }
                    catch { }
                }

                // Zapisz w bazie danych
                var trashItem = new Models.TrashItem
                {
                    UserId = userId.Value,
                    OriginalPath = path,
                    TrashPath = trashPath,
                    Name = fileName,
                    Type = isFolder ? "folder" : "file",
                    Size = size,
                    DeletedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(30) // Automatyczne usunięcie po 30 dniach
                };

                _context.TrashItems.Add(trashItem);
                await _context.SaveChangesAsync();

                // Loguj
                await LogAuditEvent(userId.Value, isFolder ? "FolderMovedToTrash" : "FileMovedToTrash", $"Moved to trash: {path}", Models.AuditLogSeverity.Info);

                return Ok(new { Message = isFolder ? "Folder został przeniesiony do kosza" : "Plik został przeniesiony do kosza" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Błąd podczas przenoszenia do kosza: {path}");
                return StatusCode(500, "Wystąpił błąd podczas przenoszenia do kosza");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas usuwania: {path}");
            return StatusCode(500, $"Wystąpił błąd podczas usuwania {(isFolder ? "folderu" : "pliku")}");
        }
    }

    /// <summary>
    /// Sprawdza czy użytkownik ma dostęp do danej ścieżki
    /// </summary>
    private async Task<bool> CheckAccessAsync(string path, string? authorization)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string? userFolderPath = await GetUserFolderPathAsync(authorization);

        // Wszyscy użytkownicy (w tym admin) mają dostęp tylko do swojego folderu i podfolderów
        if (!string.IsNullOrEmpty(userFolderPath))
        {
            // Normalizuj ścieżki (zamień backslashe na forward slashe dla porównania)
            string normalizedPath = path.Replace('\\', '/').TrimStart('/');
            string normalizedUserPath = userFolderPath.Replace('\\', '/').TrimStart('/');
            
            return normalizedPath.StartsWith(normalizedUserPath, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private async Task<string?> GetUserFolderPathAsync(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
            return null;

        var token = authorization.Replace("Bearer ", "");
        var parts = token.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int userId))
            return null;

        var user = await _context.Users.FindAsync(userId);
        return user?.IsActive == true ? user.SmbFolderPath : null;
    }

    private async Task<bool> IsAdminAsync(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
            return false;

        var token = authorization.Replace("Bearer ", "");
        var parts = token.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int userId))
            return false;

        var user = await _context.Users.FindAsync(userId);
        return user?.IsActive == true && user.Role == Models.UserRole.Admin;
    }

    private async Task<int?> GetUserIdAsync(string? authorization)
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

    private async Task LogAuditEvent(int userId, string action, string details, Models.AuditLogSeverity severity = Models.AuditLogSeverity.Info)
    {
        var log = new Models.AuditLog
        {
            UserId = userId,
            Action = action,
            Details = details,
            Severity = severity,
            Timestamp = DateTime.UtcNow,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers["User-Agent"].ToString()
        };

        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Pobiera listę plików w koszu
    /// </summary>
    [HttpGet("trash")]
    public async Task<ActionResult<IEnumerable<TrashItemDto>>> GetTrashItems([FromHeader] string? authorization)
    {
        var userId = await GetUserIdAsync(authorization);
        if (!userId.HasValue)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        try
        {
            var trashItems = await _context.TrashItems
                .Where(t => t.UserId == userId.Value)
                .OrderByDescending(t => t.DeletedAt)
                .Select(t => new TrashItemDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    OriginalPath = t.OriginalPath,
                    Type = t.Type,
                    Size = t.Size,
                    DeletedAt = t.DeletedAt,
                    ExpiresAt = t.ExpiresAt
                })
                .ToListAsync();

            return Ok(trashItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trash items");
            return StatusCode(500, "Błąd podczas pobierania kosza.");
        }
    }

    /// <summary>
    /// Przywraca plik/folder z kosza
    /// </summary>
    [HttpPost("trash/{id}/restore")]
    public async Task<ActionResult> RestoreFromTrash(int id, [FromHeader] string? authorization)
    {
        var userId = await GetUserIdAsync(authorization);
        if (!userId.HasValue)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        try
        {
            var trashItem = await _context.TrashItems
                .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value);

            if (trashItem == null)
            {
                return NotFound("Element nie został znaleziony w koszu.");
            }

            // Sprawdź czy oryginalna lokalizacja jest dostępna
            var user = await _context.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return NotFound("Użytkownik nie został znaleziony.");
            }

            // Przenieś z powrotem
            try
            {
                if (trashItem.Type == "folder")
                {
                    // Dla folderów - uproszczone
                    _smbService.CreateDirectory(trashItem.OriginalPath);
                }
                else
                {
                    // Dla plików
                    var sourceStream = _smbService.GetFileStream(trashItem.TrashPath);
                    await _smbService.SaveFileAsync(trashItem.OriginalPath, sourceStream);
                    sourceStream.Close();
                }

                // Usuń z kosza
                _smbService.DeleteFile(trashItem.TrashPath);

                // Usuń z bazy danych
                _context.TrashItems.Remove(trashItem);
                await _context.SaveChangesAsync();

                await LogAuditEvent(userId.Value, "ItemRestoredFromTrash", $"Restored: {trashItem.OriginalPath}", Models.AuditLogSeverity.Info);

                return Ok(new { Message = "Element został przywrócony." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error restoring from trash: {trashItem.TrashPath}");
                return StatusCode(500, "Błąd podczas przywracania elementu.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error restoring trash item {id}");
            return StatusCode(500, "Błąd podczas przywracania z kosza.");
        }
    }

    /// <summary>
    /// Usuwa trwale element z kosza
    /// </summary>
    [HttpDelete("trash/{id}")]
    public async Task<ActionResult> DeleteFromTrash(int id, [FromHeader] string? authorization)
    {
        var userId = await GetUserIdAsync(authorization);
        if (!userId.HasValue)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        try
        {
            var trashItem = await _context.TrashItems
                .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value);

            if (trashItem == null)
            {
                return NotFound("Element nie został znaleziony w koszu.");
            }

            // Usuń fizycznie
            try
            {
                if (trashItem.Type == "folder")
                {
                    _smbService.DeleteDirectory(trashItem.TrashPath);
                }
                else
                {
                    _smbService.DeleteFile(trashItem.TrashPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"File not found in trash: {trashItem.TrashPath}");
            }

            // Usuń z bazy danych
            _context.TrashItems.Remove(trashItem);
            await _context.SaveChangesAsync();

            await LogAuditEvent(userId.Value, "ItemDeletedFromTrash", $"Permanent delete from trash: {trashItem.OriginalPath}", Models.AuditLogSeverity.Info);

            return Ok(new { Message = "Element został trwale usunięty z kosza." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error deleting trash item {id}");
            return StatusCode(500, "Błąd podczas usuwania z kosza.");
        }
    }

    /// <summary>
    /// Przenosi pliki i foldery do innego folderu
    /// </summary>
    [HttpPost("move")]
    public async Task<ActionResult<MoveFilesResponse>> MoveFiles(
        [FromBody] MoveFilesRequest request,
        [FromHeader] string? authorization = null)
    {
        try
        {
            if (request.Items == null || request.Items.Count == 0)
            {
                return BadRequest(new { Message = "Brak elementów do przeniesienia." });
            }

            if (string.IsNullOrEmpty(request.DestinationPath))
            {
                return BadRequest(new { Message = "Ścieżka docelowa jest wymagana." });
            }

            var userId = await GetUserIdAsync(authorization);
            if (!userId.HasValue)
            {
                return Unauthorized("Brak autoryzacji.");
            }

            // Sprawdź dostęp do wszystkich elementów źródłowych i docelowych
            foreach (var item in request.Items)
            {
                if (!await CheckAccessAsync(item.Path, authorization))
                {
                    return StatusCode(403, new { Message = $"Brak dostępu do: {item.Path}" });
                }
            }

            if (!await CheckAccessAsync(request.DestinationPath, authorization))
            {
                return StatusCode(403, new { Message = "Brak dostępu do folderu docelowego." });
            }

            var movedItems = new List<MovedItemInfo>();
            var failedItems = new List<FailedItemInfo>();
            long totalSize = 0;
            long movedSize = 0;

            // Oblicz całkowity rozmiar
            foreach (var item in request.Items)
            {
                if (item.IsFolder)
                {
                    try
                    {
                        var files = _smbService.GetAllFilesRecursive(item.Path).ToList();
                        totalSize += files.Sum(f => f.File.Length);
                    }
                    catch
                    {
                        // Jeśli nie można obliczyć, użyj 0
                    }
                }
                else
                {
                    try
                    {
                        var fileInfo = _smbService.GetFileInfo(item.Path);
                        if (fileInfo != null)
                        {
                            totalSize += fileInfo.Length;
                        }
                    }
                    catch
                    {
                        // Jeśli nie można obliczyć, użyj 0
                    }
                }
            }

            // Przenieś każdy element
            foreach (var item in request.Items)
            {
                try
                {
                    var fileName = Path.GetFileName(item.Path);
                    var destinationPath = Path.Combine(request.DestinationPath, fileName).Replace('\\', '/');

                    // Sprawdź czy element docelowy już istnieje
                    bool exists = item.IsFolder
                        ? _smbService.DirectoryExists(destinationPath)
                        : _smbService.GetFileInfo(destinationPath) != null;

                    if (exists && !request.Overwrite)
                    {
                        failedItems.Add(new FailedItemInfo
                        {
                            Path = item.Path,
                            Name = fileName,
                            Reason = "Element już istnieje w folderze docelowym."
                        });
                        continue;
                    }

                    // Przenieś element z postępem
                    var progress = new Progress<long>(bytes =>
                    {
                        movedSize += bytes;
                        // Możemy tutaj wysyłać postęp przez SignalR lub inny mechanizm
                        // Na razie zwracamy postęp w odpowiedzi
                    });

                    bool success;
                    if (item.IsFolder)
                    {
                        success = await _smbService.MoveDirectoryAsync(item.Path, destinationPath, progress);
                    }
                    else
                    {
                        success = await _smbService.MoveFileAsync(item.Path, destinationPath, progress);
                    }

                    if (success)
                    {
                        movedItems.Add(new MovedItemInfo
                        {
                            OriginalPath = item.Path,
                            DestinationPath = destinationPath,
                            Name = fileName,
                            IsFolder = item.IsFolder
                        });

                        // Loguj
                        await LogAuditEvent(userId.Value, item.IsFolder ? "FolderMoved" : "FileMoved", 
                            $"Moved: {item.Path} -> {destinationPath}", Models.AuditLogSeverity.Info);
                    }
                    else
                    {
                        failedItems.Add(new FailedItemInfo
                        {
                            Path = item.Path,
                            Name = fileName,
                            Reason = "Nie można przenieść elementu."
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Błąd podczas przenoszenia: {item.Path}");
                    failedItems.Add(new FailedItemInfo
                    {
                        Path = item.Path,
                        Name = Path.GetFileName(item.Path),
                        Reason = ex.Message
                    });
                }
            }

            var response = new MoveFilesResponse
            {
                MovedItems = movedItems,
                FailedItems = failedItems,
                TotalItems = request.Items.Count,
                MovedCount = movedItems.Count,
                FailedCount = failedItems.Count,
                TotalSize = totalSize,
                MovedSize = movedSize
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd podczas przenoszenia plików");
            return StatusCode(500, new { Message = "Wystąpił błąd podczas przenoszenia plików." });
        }
    }
}

public class TrashItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime DeletedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class FileInfoDto
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Extension { get; set; } = string.Empty;
}

public class FileSystemItemDto
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "file" lub "folder"
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Extension { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty; // Pełna ścieżka względna (dla wyszukiwania)
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

public class FileUploadResponse
{
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class MoveFilesRequest
{
    public List<MoveItemRequest> Items { get; set; } = new();
    public string DestinationPath { get; set; } = string.Empty;
    public bool Overwrite { get; set; } = false;
}

public class MoveItemRequest
{
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
}

public class MoveFilesResponse
{
    public List<MovedItemInfo> MovedItems { get; set; } = new();
    public List<FailedItemInfo> FailedItems { get; set; } = new();
    public int TotalItems { get; set; }
    public int MovedCount { get; set; }
    public int FailedCount { get; set; }
    public long TotalSize { get; set; }
    public long MovedSize { get; set; }
}

public class MovedItemInfo
{
    public string OriginalPath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
}

public class FailedItemInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
