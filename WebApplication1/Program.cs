using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Serialization;
using WebApplication1.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using System.Text;

// Rejestruj CodePagesEncodingProvider dla obsługi kodowań Windows (CP1250, CP852, etc.)
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();

// Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Name = "SMBFileManager.Auth";
    options.ExpireTimeSpan = TimeSpan.FromHours(24);
    options.SlidingExpiration = true;
});

// Configure request size limits for file uploads (1000GB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1000L * 1024 * 1024 * 1024; // 1000GB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBoundaryLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Configure Kestrel server limits
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 1000L * 1024 * 1024 * 1024; // 1000GB
    
    // HTTP
    options.ListenAnyIP(5087, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

// Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register SMB Service
builder.Services.AddSingleton<SmbService>();

// Utwórz folder admina przy starcie (jeśli nie istnieje)
builder.Services.AddHostedService<AdminFolderInitializer>();

// CORS - skonfigurowany dla aplikacji mobilnej
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader()
              .SetPreflightMaxAge(TimeSpan.FromSeconds(3600)); // Cache preflight requests
    });
});

// Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS musi być przed innymi middleware
app.UseCors();

// HTTPS redirection - przekieruj HTTP na HTTPS jeśli HTTPS jest dostępne
var certPath = Path.Combine(app.Environment.ContentRootPath, "certificates", "SMB-FileManager-Local.pfx");
if (File.Exists(certPath))
{
    app.UseHttpsRedirection();
    Console.WriteLine("✓ HTTPS redirection włączony");
}
else
{
    Console.WriteLine("⚠ HTTPS redirection wyłączony - brak certyfikatu");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Ensure database is created and seeded
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        context.Database.EnsureCreated();
        
        // Seed initial admin user if not exists
        if (!context.Users.Any(u => u.Username == "admin"))
        {
            var adminUser = new WebApplication1.Models.User
            {
                Username = "admin",
                Email = "admin@example.com",
                PasswordHash = HashPassword("admin123"),
                Role = WebApplication1.Models.UserRole.Admin,
                IsApproved = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                ApprovedAt = DateTime.UtcNow,
                SmbFolderPath = "users/admin"
            };
            context.Users.Add(adminUser);
            context.SaveChanges();
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while seeding the database.");
    }
}

app.Run();

static string HashPassword(string password)
{
    using (var sha256 = System.Security.Cryptography.SHA256.Create())
    {
        var hashedBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }
}