using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using WebApplication1.Data;
using WebApplication1.Models;
using WebApplication1.Services;
using OtpNet;
using QRCoder;
using System.Text.Json;
using User = WebApplication1.Models.User;

namespace WebApplication1;

[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly SmbService _smbService;
    private readonly ILogger<UserController> _logger;
    private readonly IConfiguration _configuration;

    public UserController(ApplicationDbContext context, SmbService smbService, ILogger<UserController> logger, IConfiguration configuration)
    {
        _context = context;
        _smbService = smbService;
        _logger = logger;
        _configuration = configuration;
    }

    private static string HashPassword(string password)
    {
        using (var sha256 = SHA256.Create())
        {
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hashedBytes);
        }
    }

    private static bool VerifyPassword(string password, string hash)
    {
        var hashOfInput = HashPassword(password);
        return hashOfInput == hash;
    }

    /// <summary>
    /// Rejestracja nowego użytkownika
    /// </summary>
    [HttpPost("register")]
    public async Task<ActionResult<RegisterResponse>> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || 
            string.IsNullOrWhiteSpace(request.Email) || 
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new RegisterResponse
            {
                Success = false,
                Message = "Nazwa użytkownika, email i hasło są wymagane."
            });
        }

        // Sprawdź czy użytkownik już istnieje
        if (await _context.Users.AnyAsync(u => u.Username == request.Username || u.Email == request.Email))
        {
            return BadRequest(new RegisterResponse
            {
                Success = false,
                Message = "Użytkownik o podanej nazwie lub emailu już istnieje."
            });
        }

        // Utwórz nowego użytkownika (niezatwierdzonego)
        var newUser = new User
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = HashPassword(request.Password),
            Role = UserRole.User,
            IsApproved = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            SmbFolderPath = $"users/{request.Username}"
        };

        _context.Users.Add(newUser);
        await _context.SaveChangesAsync();

        // Loguj rejestrację
        await LogAuditEvent(null, "UserRegistration", $"User {request.Username} registered", AuditLogSeverity.Info);

        return Ok(new RegisterResponse
        {
            Success = true,
            Message = "Rejestracja pomyślna. Konto oczekuje na zatwierdzenie przez administratora."
        });
    }

    /// <summary>
    /// Logowanie użytkownika
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new LoginResponse
            {
                Success = false,
                Message = "Nazwa użytkownika i hasło są wymagane."
            });
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username && u.IsActive);
        if (user == null || !VerifyPassword(request.Password, user.PasswordHash))
        {
            await LogAuditEvent(null, "LoginFailed", $"Failed login attempt for username: {request.Username}", AuditLogSeverity.Warning, GetClientIpAddress());
            return Unauthorized(new LoginResponse
            {
                Success = false,
                Message = "Nieprawidłowa nazwa użytkownika lub hasło."
            });
        }

        if (!user.IsApproved)
        {
            await LogAuditEvent(user.Id, "LoginBlocked", $"Login blocked - account not approved for user: {request.Username}", AuditLogSeverity.Warning, GetClientIpAddress());
            return Unauthorized(new LoginResponse
            {
                Success = false,
                Message = "Konto nie zostało jeszcze zatwierdzone przez administratora."
            });
        }

        // Sprawdź czy użytkownik ma włączone 2FA
        if (user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret))
        {
            // Jeśli nie podano kodu 2FA, zwróć informację, że jest wymagany
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
            {
                return Ok(new LoginResponse
                {
                    Success = false,
                    RequiresTwoFactor = true,
                    Message = "Wymagany kod 2FA."
                });
            }

            // Weryfikuj kod 2FA
            var totp = new Totp(Base32Encoding.ToBytes(user.TwoFactorSecret));
            if (!totp.VerifyTotp(request.TwoFactorCode, out _, new VerificationWindow(1, 1)))
            {
                await LogAuditEvent(user.Id, "TwoFactorFailed", $"Failed 2FA verification for user: {request.Username}", AuditLogSeverity.Warning, GetClientIpAddress());
                return Unauthorized(new LoginResponse
                {
                    Success = false,
                    RequiresTwoFactor = true,
                    Message = "Nieprawidłowy kod 2FA."
                });
            }
        }

        // Aktualizuj datę ostatniego logowania
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Utwórz sesję
        var token = $"token-{user.Id}-{Guid.NewGuid()}";
        var session = new UserSession
        {
            UserId = user.Id,
            Token = token,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            IpAddress = GetClientIpAddress(),
            UserAgent = Request.Headers["User-Agent"].ToString(),
            IsActive = true
        };

        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync();

        // Loguj pomyślne logowanie
        await LogAuditEvent(user.Id, "LoginSuccess", $"User {user.Username} logged in", AuditLogSeverity.Info, GetClientIpAddress());

        var response = new LoginResponse
        {
            Success = true,
            Token = token,
            Message = "Logowanie pomyślne",
            User = new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                Role = user.Role.ToString(),
                IsApproved = user.IsApproved,
                SmbFolderPath = user.SmbFolderPath,
                TwoFactorEnabled = user.TwoFactorEnabled
            }
        };
        return Ok(response);
    }

    /// <summary>
    /// Pobiera listę użytkowników oczekujących na zatwierdzenie (tylko dla admina)
    /// </summary>
    [HttpGet("pending")]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetPendingUsers()
    {
        var pendingUsers = await _context.Users
            .Where(u => !u.IsApproved && u.IsActive)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                Role = u.Role.ToString(),
                IsApproved = u.IsApproved,
                CreatedAt = u.CreatedAt,
                SmbFolderPath = u.SmbFolderPath,
                TwoFactorEnabled = u.TwoFactorEnabled
            })
            .ToListAsync();

        return Ok(pendingUsers);
    }

    /// <summary>
    /// Zatwierdza konto użytkownika i tworzy folder SMB (tylko dla admina)
    /// </summary>
    [HttpPost("approve/{userId}")]
    public async Task<ActionResult> ApproveUser(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Użytkownik nie został znaleziony.");
        }

        if (user.IsApproved)
        {
            return BadRequest("Konto użytkownika jest już zatwierdzone.");
        }

        // Zatwierdź konto
        user.IsApproved = true;
        user.ApprovedAt = DateTime.UtcNow;

        // Utwórz folder SMB dla użytkownika
        try
        {
            _smbService.CreateDirectory(user.SmbFolderPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating SMB folder for user {UserId}", userId);
            // Jeśli nie udało się utworzyć folderu, cofnij zatwierdzenie
            user.IsApproved = false;
            user.ApprovedAt = null;
            await _context.SaveChangesAsync();
            return StatusCode(500, $"Błąd podczas tworzenia folderu SMB: {ex.Message}");
        }

        await _context.SaveChangesAsync();

        // Loguj zatwierdzenie
        await LogAuditEvent(userId, "UserApproved", $"User {user.Username} approved by admin", AuditLogSeverity.Info);

        return Ok(new { Message = "Konto zostało zatwierdzone i folder SMB został utworzony." });
    }

    /// <summary>
    /// Pobiera ID użytkownika z tokenu
    /// </summary>
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

    /// <summary>
    /// Pobiera informacje o zalogowanym użytkowniku
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> GetCurrentUser([FromHeader] string? authorization)
    {
        var token = authorization?.Replace("Bearer ", "");
        if (string.IsNullOrEmpty(token))
        {
            return Unauthorized("Brak tokenu autoryzacji.");
        }

        // Parsuj token (format: token-{userId}-{guid})
        var parts = token.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int userId))
        {
            return Unauthorized("Nieprawidłowy token.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null || !user.IsActive)
        {
            return NotFound("Użytkownik nie został znaleziony.");
        }

        return Ok(new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            Role = user.Role.ToString(),
            IsApproved = user.IsApproved,
            SmbFolderPath = user.SmbFolderPath,
            TwoFactorEnabled = user.TwoFactorEnabled
        });
    }

    /// <summary>
    /// Pobiera listę wszystkich użytkowników (tylko dla admina)
    /// </summary>
    [HttpGet("all")]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetAllUsers()
    {
        var users = await _context.Users
            .Where(u => u.IsActive)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                Role = u.Role.ToString(),
                IsApproved = u.IsApproved,
                CreatedAt = u.CreatedAt,
                ApprovedAt = u.ApprovedAt,
                SmbFolderPath = u.SmbFolderPath,
                TwoFactorEnabled = u.TwoFactorEnabled,
                StorageQuota = u.StorageQuota
            })
            .ToListAsync();

        return Ok(users);
    }

    /// <summary>
    /// Pobiera ścieżkę folderu użytkownika na podstawie tokenu
    /// </summary>
    public static async Task<string?> GetUserFolderPathAsync(ApplicationDbContext context, string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
            return null;

        var token = authorization.Replace("Bearer ", "");
        var parts = token.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int userId))
            return null;

        var user = await context.Users.FindAsync(userId);
        return user?.IsActive == true ? user.SmbFolderPath : null;
    }

    /// <summary>
    /// Generuje klucz 2FA i zwraca QR code
    /// </summary>
    [HttpPost("two-factor/generate")]
    public async Task<ActionResult<TwoFactorGenerateResponse>> GenerateTwoFactor([FromHeader] string? authorization)
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

        // Generuj nowy klucz
        var key = KeyGeneration.GenerateRandomKey(20);
        var base32Key = Base32Encoding.ToString(key);

        // Generuj QR code
        var issuer = "SMB File Manager";
        var accountTitle = $"{issuer}:{user.Username}";
        var manualEntryKey = base32Key;
        var qrCodeUrl = $"otpauth://totp/{Uri.EscapeDataString(accountTitle)}?secret={manualEntryKey}&issuer={Uri.EscapeDataString(issuer)}";

        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(qrCodeUrl, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrCodeData);
        var qrCodeBytes = qrCode.GetGraphic(20);

        return Ok(new TwoFactorGenerateResponse
        {
            Success = true,
            Secret = base32Key,
            QrCodeBase64 = Convert.ToBase64String(qrCodeBytes),
            ManualEntryKey = manualEntryKey
        });
    }

    /// <summary>
    /// Weryfikuje kod 2FA i włącza 2FA dla użytkownika
    /// </summary>
    [HttpPost("two-factor/enable")]
    public async Task<ActionResult<TwoFactorEnableResponse>> EnableTwoFactor([FromHeader] string? authorization, [FromBody] TwoFactorEnableRequest request)
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

        if (string.IsNullOrWhiteSpace(request.Secret) || string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new TwoFactorEnableResponse
            {
                Success = false,
                Message = "Klucz i kod są wymagane."
            });
        }

        // Weryfikuj kod
        var totp = new Totp(Base32Encoding.ToBytes(request.Secret));
        if (!totp.VerifyTotp(request.Code, out _, new VerificationWindow(1, 1)))
        {
            return BadRequest(new TwoFactorEnableResponse
            {
                Success = false,
                Message = "Nieprawidłowy kod 2FA. Upewnij się, że wprowadziłeś aktualny kod z aplikacji autoryzacyjnej."
            });
        }

        // Włącz 2FA
        user.TwoFactorSecret = request.Secret;
        user.TwoFactorEnabled = true;
        await _context.SaveChangesAsync();

        await LogAuditEvent(userId, "TwoFactorEnabled", $"User {user.Username} enabled 2FA", AuditLogSeverity.Info);

        return Ok(new TwoFactorEnableResponse
        {
            Success = true,
            Message = "2FA zostało włączone pomyślnie."
        });
    }

    /// <summary>
    /// Wyłącza 2FA dla użytkownika
    /// </summary>
    [HttpPost("two-factor/disable")]
    public async Task<ActionResult<TwoFactorDisableResponse>> DisableTwoFactor([FromHeader] string? authorization, [FromBody] TwoFactorDisableRequest request)
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

        if (!user.TwoFactorEnabled)
        {
            return BadRequest(new TwoFactorDisableResponse
            {
                Success = false,
                Message = "2FA nie jest włączone dla tego konta."
            });
        }

        // Weryfikuj hasło
        if (!VerifyPassword(request.Password, user.PasswordHash))
        {
            return Unauthorized(new TwoFactorDisableResponse
            {
                Success = false,
                Message = "Nieprawidłowe hasło."
            });
        }

        // Wyłącz 2FA
        user.TwoFactorSecret = null;
        user.TwoFactorEnabled = false;
        await _context.SaveChangesAsync();

        await LogAuditEvent(userId, "TwoFactorDisabled", $"User {user.Username} disabled 2FA", AuditLogSeverity.Info);

        return Ok(new TwoFactorDisableResponse
        {
            Success = true,
            Message = "2FA zostało wyłączone pomyślnie."
        });
    }

    /// <summary>
    /// Pobiera status 2FA dla użytkownika
    /// </summary>
    [HttpGet("two-factor/status")]
    public async Task<ActionResult<TwoFactorStatusResponse>> GetTwoFactorStatus([FromHeader] string? authorization)
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

        return Ok(new TwoFactorStatusResponse
        {
            Enabled = user.TwoFactorEnabled
        });
    }

    /// <summary>
    /// Generuje plik .bat do mapowania dysku sieciowego
    /// </summary>
    [HttpGet("map-drive-script")]
    public async Task<ActionResult> GetMapDriveScript([FromHeader] string? authorization)
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

        // Pobierz konfigurację SMB
        var smbSettings = _configuration.GetSection("SmbSettings");
        var serverPath = smbSettings["ServerPath"] ?? "\\\\192.168.100.45\\sambashare";
        var username = smbSettings["Username"] ?? "";
        var password = smbSettings["Password"] ?? "";
        var domain = smbSettings["Domain"] ?? "";

        // Utwórz ścieżkę do folderu użytkownika
        var userFolderPath = $"{serverPath}\\{user.SmbFolderPath.Replace('/', '\\')}";

        // Generuj skrypt .bat z obsługą polskich znaków
        // Używamy CP1250 (Windows-1250) dla polskich znaków w CMD
        var script = "@echo off\r\n";
        script += "chcp 1250 >nul 2>&1\r\n";
        script += "echo ========================================\r\n";
        script += "echo Mapowanie dysku sieciowego SMB\r\n";
        script += "echo ========================================\r\n";
        script += "echo.\r\n";
        script += "echo Folder użytkownika: " + user.SmbFolderPath + "\r\n";
        script += "echo.\r\n";
        script += "\r\n";
        script += "REM Wybierz literę dysku (domyślnie Z:)\r\n";
        script += "set DRIVE_LETTER=Z:\r\n";
        script += "\r\n";
        script += "REM Sprawdź czy dysk jest już zmapowany\r\n";
        script += "if exist %DRIVE_LETTER% (\r\n";
        script += "    echo Dysk %DRIVE_LETTER% jest już zmapowany.\r\n";
        script += "    echo Odłączanie istniejącego mapowania...\r\n";
        script += "    net use %DRIVE_LETTER% /delete /y >nul 2>&1\r\n";
        script += ")\r\n";
        script += "\r\n";
        script += "REM Mapuj dysk sieciowy\r\n";
        script += "echo Mapowanie dysku %DRIVE_LETTER%...\r\n";
        script += "\r\n";

        if (!string.IsNullOrEmpty(domain))
        {
            script += $"net use %DRIVE_LETTER% \"{userFolderPath}\" /user:{domain}\\{username} {password} /persistent:yes\r\n";
        }
        else if (!string.IsNullOrEmpty(username))
        {
            script += $"net use %DRIVE_LETTER% \"{userFolderPath}\" /user:{username} {password} /persistent:yes\r\n";
        }
        else
        {
            script += $"net use %DRIVE_LETTER% \"{userFolderPath}\" /persistent:yes\r\n";
        }

        script += "\r\n";
        script += "if %ERRORLEVEL% EQU 0 (\r\n";
        script += "    echo.\r\n";
        script += "    echo ========================================\r\n";
        script += "    echo Mapowanie zakończone pomyślnie!\r\n";
        script += "    echo Dysk dostępny jako: %DRIVE_LETTER%\r\n";
        script += "    echo ========================================\r\n";
        script += "    echo.\r\n";
        script += "    pause\r\n";
        script += "    explorer %DRIVE_LETTER%\r\n";
        script += ") else (\r\n";
        script += "    echo.\r\n";
        script += "    echo ========================================\r\n";
        script += "    echo BŁĄD: Nie udało się zmapować dysku!\r\n";
        script += "    echo Sprawdź:\r\n";
        script += "    echo - Czy serwer SMB jest dostępny\r\n";
        script += "    echo - Czy dane logowania są poprawne\r\n";
        script += "    echo - Czy masz uprawnienia do folderu\r\n";
        script += "    echo ========================================\r\n";
        script += "    echo.\r\n";
        script += "    pause\r\n";
        script += ")\r\n";

        // Używamy CP1250 (Windows-1250) dla polskich znaków w CMD
        // Wymaga rejestracji CodePagesEncodingProvider w Program.cs
        var encoding = Encoding.GetEncoding(1250);
        var bytes = encoding.GetBytes(script);
        
        return File(bytes, "application/x-msdownload", $"mapuj_dysk_{user.Username}.bat");
    }

    /// <summary>
    /// Zmienia hasło użytkownika
    /// </summary>
    [HttpPost("change-password")]
    public async Task<ActionResult<ChangePasswordResponse>> ChangePassword([FromHeader] string? authorization, [FromBody] ChangePasswordRequest request)
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

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new ChangePasswordResponse
            {
                Success = false,
                Message = "Obecne hasło i nowe hasło są wymagane."
            });
        }

        if (request.NewPassword.Length < 6)
        {
            return BadRequest(new ChangePasswordResponse
            {
                Success = false,
                Message = "Nowe hasło musi mieć co najmniej 6 znaków."
            });
        }

        // Weryfikuj obecne hasło
        if (!VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            await LogAuditEvent(userId, "PasswordChangeFailed", $"Failed password change attempt for user: {user.Username}", AuditLogSeverity.Warning, GetClientIpAddress());
            return Unauthorized(new ChangePasswordResponse
            {
                Success = false,
                Message = "Nieprawidłowe obecne hasło."
            });
        }

        // Zmień hasło
        user.PasswordHash = HashPassword(request.NewPassword);
        await _context.SaveChangesAsync();

        await LogAuditEvent(userId, "PasswordChanged", $"User {user.Username} changed password", AuditLogSeverity.Info, GetClientIpAddress());

        return Ok(new ChangePasswordResponse
        {
            Success = true,
            Message = "Hasło zostało zmienione pomyślnie."
        });
    }

    private async Task LogAuditEvent(int? userId, string action, string details, AuditLogSeverity severity = AuditLogSeverity.Info, string? ipAddress = null)
    {
        var log = new AuditLog
        {
            UserId = userId,
            Action = action,
            Details = details,
            Severity = severity,
            Timestamp = DateTime.UtcNow,
            IpAddress = ipAddress ?? GetClientIpAddress(),
            UserAgent = Request.Headers["User-Agent"].ToString()
        };

        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    private string? GetClientIpAddress()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Pobiera quota użytkownika
    /// </summary>
    [HttpGet("quota")]
    public async Task<ActionResult<QuotaResponse>> GetUserQuota([FromHeader] string? authorization)
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

        // Oblicz użycie
        long used = 0;
        try
        {
            var files = _smbService.GetAllFilesRecursive(user.SmbFolderPath).ToList();
            used = files.Sum(f => f.Item1.Length);
        }
        catch { }

        return Ok(new QuotaResponse
        {
            Quota = user.StorageQuota,
            Used = used,
            Remaining = user.StorageQuota.HasValue ? Math.Max(0, user.StorageQuota.Value - used) : null,
            PercentUsed = user.StorageQuota.HasValue && user.StorageQuota.Value > 0 
                ? (double)used / user.StorageQuota.Value * 100 
                : 0
        });
    }

    /// <summary>
    /// Ustawia quota użytkownika (tylko dla admina)
    /// </summary>
    [HttpPost("quota/{userId}")]
    public async Task<ActionResult> SetUserQuota(int userId, [FromHeader] string? authorization, [FromBody] SetQuotaRequest request)
    {
        var adminId = await GetUserIdFromTokenAsync(authorization);
        if (adminId == null)
        {
            return Unauthorized("Brak autoryzacji.");
        }

        var admin = await _context.Users.FindAsync(adminId);
        if (admin == null || admin.Role != UserRole.Admin)
        {
            return Forbid("Tylko administrator może ustawiać quota.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("Użytkownik nie został znaleziony.");
        }

        // Quota w bajtach, null = brak limitu
        user.StorageQuota = request.QuotaBytes > 0 ? request.QuotaBytes : null;
        await _context.SaveChangesAsync();

        await LogAuditEvent(adminId, "QuotaSet", $"Set quota for user {user.Username}: {request.QuotaBytes} bytes", AuditLogSeverity.Info);

        return Ok(new { Message = "Quota została ustawiona." });
    }


}

public class QuotaResponse
{
    public long? Quota { get; set; }
    public long Used { get; set; }
    public long? Remaining { get; set; }
    public double PercentUsed { get; set; }
}

public class SetQuotaRequest
{
    public long QuotaBytes { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsApproved { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string SmbFolderPath { get; set; } = string.Empty;
    public bool TwoFactorEnabled { get; set; }
    public long? StorageQuota { get; set; }
}

public class RegisterRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class RegisterResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? TwoFactorCode { get; set; }
}

public class LoginResponse
{
    public bool Success { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public UserDto? User { get; set; }
    public bool RequiresTwoFactor { get; set; } = false;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangePasswordResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TwoFactorGenerateResponse
{
    public bool Success { get; set; }
    public string Secret { get; set; } = string.Empty;
    public string QrCodeBase64 { get; set; } = string.Empty;
    public string ManualEntryKey { get; set; } = string.Empty;
}

public class TwoFactorEnableRequest
{
    public string Secret { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class TwoFactorEnableResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TwoFactorDisableRequest
{
    public string Password { get; set; } = string.Empty;
}

public class TwoFactorDisableResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TwoFactorStatusResponse
{
    public bool Enabled { get; set; }
}

