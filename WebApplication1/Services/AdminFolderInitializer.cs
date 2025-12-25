using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApplication1.Data;
using WebApplication1.Models;
using WebApplication1.Services;

namespace WebApplication1.Services;

public class AdminFolderInitializer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AdminFolderInitializer> _logger;

    public AdminFolderInitializer(IServiceProvider serviceProvider, ILogger<AdminFolderInitializer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var smbService = scope.ServiceProvider.GetRequiredService<SmbService>();

            // Pobierz admina z bazy danych
            var admin = await context.Users
                .FirstOrDefaultAsync(u => u.Role == UserRole.Admin && u.IsActive, cancellationToken);

            if (admin != null && !string.IsNullOrEmpty(admin.SmbFolderPath))
            {
                if (!smbService.DirectoryExists(admin.SmbFolderPath))
                {
                    smbService.CreateDirectory(admin.SmbFolderPath);
                    _logger.LogInformation($"Folder admina został utworzony: {admin.SmbFolderPath}");
                }
                else
                {
                    _logger.LogInformation($"Folder admina już istnieje: {admin.SmbFolderPath}");
                }
            }
            else
            {
                _logger.LogWarning("Admin user not found in database. Cannot initialize admin folder.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd podczas inicjalizacji folderu admina");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

