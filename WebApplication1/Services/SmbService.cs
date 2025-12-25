using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WebApplication1.Services;

public class SmbService
{
    private readonly string _smbPath;
    private readonly ILogger<SmbService> _logger;

    public SmbService(IConfiguration configuration, ILogger<SmbService> logger)
    {
        _logger = logger;
        
        // Hardcoded ustawienia SMB - nie bierz z appsettings.json
        _smbPath = "\\\\192.168.100.45\\sambashare";
        // Username: ms, Password: 3505, Domain: (puste)

        _logger.LogInformation($"Konfiguracja SMB (hardcoded): {_smbPath}");
    }

    public bool DirectoryExists()
    {
        try
        {
            return Directory.Exists(_smbPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas sprawdzania istnienia katalogu SMB: {_smbPath}");
            return false;
        }
    }

    public bool DirectoryExists(string subPath)
    {
        try
        {
            var fullPath = CombineSmbPath(_smbPath, subPath);
            return Directory.Exists(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas sprawdzania istnienia katalogu: {subPath}");
            return false;
        }
    }

    public IEnumerable<FileInfo> GetFiles()
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var directory = new DirectoryInfo(_smbPath);
            return directory.GetFiles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas pobierania listy plików z SMB: {_smbPath}");
            throw;
        }
    }

    public (IEnumerable<FileInfo> Files, IEnumerable<DirectoryInfo> Directories) GetFilesAndDirectories(string? subPath = null)
    {
        try
        {
            var targetPath = string.IsNullOrEmpty(subPath) ? _smbPath : CombineSmbPath(_smbPath, subPath);
            
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException($"Katalog nie istnieje: {targetPath}");
            }
            
            var directory = new DirectoryInfo(targetPath);
            
            // Pobierz pliki
            FileInfo[] files;
            try
            {
                files = directory.GetFiles();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting files");
                files = Array.Empty<FileInfo>();
            }
            
            // Pobierz foldery
            DirectoryInfo[] directories;
            try
            {
                directories = directory.GetDirectories();
            }
            catch (Exception)
            {
                // Alternatywna metoda
                try
                {
                    var dirPaths = Directory.EnumerateDirectories(targetPath);
                    var dirList = new List<DirectoryInfo>();
                    foreach (var dirPath in dirPaths)
                    {
                        try
                        {
                            dirList.Add(new DirectoryInfo(dirPath));
                        }
                        catch
                        {
                            // Ignoruj błędy dla pojedynczych folderów
                        }
                    }
                    directories = dirList.ToArray();
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "Both methods failed to get directories");
                    directories = Array.Empty<DirectoryInfo>();
                }
            }
            
            return (files, directories);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas pobierania listy plików i folderów z SMB");
            throw;
        }
    }

    public FileInfo? GetFileInfo(string filePath)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullPath = CombineSmbPath(_smbPath, filePath);
            var fileInfo = new FileInfo(fullPath);
            
            return fileInfo.Exists ? fileInfo : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas pobierania informacji o pliku: {filePath}");
            throw;
        }
    }

    public Stream GetFileStream(string filePath)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullPath = CombineSmbPath(_smbPath, filePath);
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning($"Plik nie znaleziony: {filePath}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas otwierania pliku: {filePath}");
            throw;
        }
    }

    public async Task SaveFileAsync(string filePath, Stream fileStream)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullPath = CombineSmbPath(_smbPath, filePath);
            
            // Upewnij się, że katalog docelowy istnieje
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            using (var outputStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await fileStream.CopyToAsync(outputStream);
            }

            _logger.LogInformation($"Plik zapisany: {filePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas zapisywania pliku: {filePath}");
            throw;
        }
    }

    public bool CreateDirectory(string folderPath)
    {
        try
        {
            if (!DirectoryExists())
            {
                _logger.LogError($"Katalog SMB nie istnieje: {_smbPath}");
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullPath = CombineSmbPath(_smbPath, folderPath);
            
            _logger.LogInformation($"Próba utworzenia folderu: {fullPath}");
            
            if (Directory.Exists(fullPath))
            {
                _logger.LogInformation($"Folder już istnieje: {fullPath}");
                return false; // Folder już istnieje
            }
            
            // Directory.CreateDirectory tworzy wszystkie nieistniejące foldery w ścieżce
            // Ale najpierw sprawdźmy, czy folder nadrzędny istnieje
            var parentPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentPath) && !Directory.Exists(parentPath))
            {
                _logger.LogInformation($"Folder nadrzędny nie istnieje, próba utworzenia: {parentPath}");
                // Spróbuj utworzyć folder nadrzędny rekurencyjnie
                CreateDirectoryRecursive(parentPath);
            }
            
            Directory.CreateDirectory(fullPath);
            
            // Sprawdź, czy folder został faktycznie utworzony
            if (Directory.Exists(fullPath))
            {
                _logger.LogInformation($"Folder utworzony pomyślnie: {fullPath}");
                return true;
            }
            else
            {
                _logger.LogWarning($"Folder nie został utworzony pomimo braku błędu: {fullPath}");
                return false;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, $"Brak uprawnień do utworzenia folderu: {folderPath}. Sprawdź uprawnienia użytkownika SMB.");
            throw new UnauthorizedAccessException($"Brak uprawnień do utworzenia folderu: {folderPath}. Sprawdź uprawnienia użytkownika SMB.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, $"Katalog nadrzędny nie istnieje dla: {folderPath}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas tworzenia folderu: {folderPath}. Pełna ścieżka: {CombineSmbPath(_smbPath, folderPath)}");
            throw;
        }
    }

    /// <summary>
    /// Rekurencyjnie tworzy wszystkie nieistniejące foldery w ścieżce
    /// </summary>
    private void CreateDirectoryRecursive(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (Directory.Exists(path))
                return;

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                CreateDirectoryRecursive(parent);
            }

            Directory.CreateDirectory(path);
            _logger.LogInformation($"Utworzono folder nadrzędny: {path}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas rekurencyjnego tworzenia folderu: {path}");
            throw;
        }
    }

    public bool DeleteFile(string filePath)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullPath = CombineSmbPath(_smbPath, filePath);
            
            if (!File.Exists(fullPath))
            {
                return false;
            }

            File.Delete(fullPath);
            _logger.LogInformation($"Plik usunięty: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas usuwania pliku: {filePath}");
            throw;
        }
    }

    /// <summary>
    /// Rekurencyjnie pobiera wszystkie pliki i foldery z podanej ścieżki
    /// </summary>
    public IEnumerable<(FileInfo File, string RelativePath)> GetAllFilesRecursive(string? subPath = null)
    {
        try
        {
            var targetPath = string.IsNullOrEmpty(subPath) ? _smbPath : CombineSmbPath(_smbPath, subPath);
            
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException($"Katalog nie istnieje: {targetPath}");
            }

            var results = new List<(FileInfo, string)>();
            var basePath = string.IsNullOrEmpty(subPath) ? _smbPath : CombineSmbPath(_smbPath, subPath);
            
            SearchFilesRecursive(basePath, subPath ?? "", results);
            
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas rekurencyjnego pobierania plików z: {subPath}");
            throw;
        }
    }

    /// <summary>
    /// Rekurencyjnie pobiera wszystkie foldery z podanej ścieżki
    /// </summary>
    public IEnumerable<(DirectoryInfo Directory, string RelativePath)> GetAllDirectoriesRecursive(string? subPath = null)
    {
        try
        {
            var targetPath = string.IsNullOrEmpty(subPath) ? _smbPath : CombineSmbPath(_smbPath, subPath);
            
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException($"Katalog nie istnieje: {targetPath}");
            }

            var results = new List<(DirectoryInfo, string)>();
            var basePath = string.IsNullOrEmpty(subPath) ? _smbPath : CombineSmbPath(_smbPath, subPath);
            
            SearchDirectoriesRecursive(basePath, subPath ?? "", results);
            
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas rekurencyjnego pobierania folderów z: {subPath}");
            throw;
        }
    }

    private void SearchFilesRecursive(string directoryPath, string relativeBasePath, List<(FileInfo, string)> results)
    {
        try
        {
            var directory = new DirectoryInfo(directoryPath);
            
            // Pobierz pliki w bieżącym katalogu
            try
            {
                foreach (var file in directory.GetFiles())
                {
                    var relativePath = string.IsNullOrEmpty(relativeBasePath) 
                        ? file.Name 
                        : Path.Combine(relativeBasePath, file.Name).Replace('\\', '/');
                    results.Add((file, relativePath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Nie można pobrać plików z: {directoryPath}");
            }
            
            // Rekurencyjnie przeszukaj podfoldery
            try
            {
                foreach (var subDir in directory.GetDirectories())
                {
                    var newRelativeBase = string.IsNullOrEmpty(relativeBasePath) 
                        ? subDir.Name 
                        : Path.Combine(relativeBasePath, subDir.Name).Replace('\\', '/');
                    SearchFilesRecursive(subDir.FullName, newRelativeBase, results);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Nie można przeszukać podfolderu: {directoryPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Błąd podczas przeszukiwania: {directoryPath}");
        }
    }

    private void SearchDirectoriesRecursive(string directoryPath, string relativeBasePath, List<(DirectoryInfo, string)> results)
    {
        try
        {
            var directory = new DirectoryInfo(directoryPath);
            
            // Pobierz foldery w bieżącym katalogu
            try
            {
                foreach (var subDir in directory.GetDirectories())
                {
                    var relativePath = string.IsNullOrEmpty(relativeBasePath) 
                        ? subDir.Name 
                        : Path.Combine(relativeBasePath, subDir.Name).Replace('\\', '/');
                    results.Add((subDir, relativePath));
                    
                    // Rekurencyjnie przeszukaj podfoldery
                    SearchDirectoriesRecursive(subDir.FullName, relativePath, results);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Nie można pobrać folderów z: {directoryPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Błąd podczas przeszukiwania folderów: {directoryPath}");
        }
    }

    public bool DeleteDirectory(string folderPath)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullPath = CombineSmbPath(_smbPath, folderPath);
            
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            Directory.Delete(fullPath, true); // true = usuń rekurencyjnie
            _logger.LogInformation($"Folder usunięty: {folderPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas usuwania folderu: {folderPath}");
            throw;
        }
    }

    /// <summary>
    /// Oblicza checksum SHA256 dla pliku
    /// </summary>
    public string CalculateSha256Hash(string filePath)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullPath = CombineSmbPath(_smbPath, filePath);
            
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Plik nie istnieje: {filePath}");
            }

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                using (var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var hashBytes = sha256.ComputeHash(fileStream);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas obliczania SHA256 dla pliku: {filePath}");
            throw;
        }
    }

    /// <summary>
    /// Przenosi plik do innego folderu
    /// </summary>
    public async Task<bool> MoveFileAsync(string sourcePath, string destinationPath, IProgress<long>? progress = null)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullSourcePath = CombineSmbPath(_smbPath, sourcePath);
            var fullDestinationPath = CombineSmbPath(_smbPath, destinationPath);

            if (!File.Exists(fullSourcePath))
            {
                return false;
            }

            // Upewnij się, że katalog docelowy istnieje
            var destinationDir = Path.GetDirectoryName(fullDestinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // Jeśli plik docelowy już istnieje, usuń go
            if (File.Exists(fullDestinationPath))
            {
                File.Delete(fullDestinationPath);
            }

            // Przenieś plik z postępem
            using (var sourceStream = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var destStream = new FileStream(fullDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920]; // 80KB buffer
                    long totalBytesRead = 0;
                    int bytesRead;

                    while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await destStream.WriteAsync(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;
                        progress?.Report(totalBytesRead);
                    }
                }
            }

            // Usuń oryginalny plik
            File.Delete(fullSourcePath);
            _logger.LogInformation($"Plik przeniesiony: {sourcePath} -> {destinationPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas przenoszenia pliku: {sourcePath} -> {destinationPath}");
            throw;
        }
    }

    /// <summary>
    /// Przenosi folder do innego miejsca (rekurencyjnie)
    /// </summary>
    public async Task<bool> MoveDirectoryAsync(string sourcePath, string destinationPath, IProgress<long>? progress = null)
    {
        try
        {
            if (!DirectoryExists())
            {
                throw new DirectoryNotFoundException($"Katalog SMB nie istnieje: {_smbPath}");
            }

            var fullSourcePath = CombineSmbPath(_smbPath, sourcePath);
            var fullDestinationPath = CombineSmbPath(_smbPath, destinationPath);

            if (!Directory.Exists(fullSourcePath))
            {
                return false;
            }

            // Jeśli folder docelowy już istnieje, usuń go
            if (Directory.Exists(fullDestinationPath))
            {
                Directory.Delete(fullDestinationPath, true);
            }

            // Utwórz folder docelowy
            Directory.CreateDirectory(fullDestinationPath);

            // Pobierz wszystkie pliki
            var allFiles = GetAllFilesRecursive(sourcePath).ToList();

            long totalSize = allFiles.Sum(f => f.File.Length);
            long movedSize = 0;
            
            // Utwórz foldery docelowe dla wszystkich podfolderów
            var allDirectories = GetAllDirectoriesRecursive(sourcePath).ToList();
            foreach (var (dir, relativePath) in allDirectories)
            {
                var destDirPath = CombineSmbPath(destinationPath, relativePath);
                var fullDestDirPath = CombineSmbPath(_smbPath, destDirPath);
                if (!Directory.Exists(fullDestDirPath))
                {
                    Directory.CreateDirectory(fullDestDirPath);
                }
            }

            // Przenieś wszystkie pliki
            foreach (var (file, relativePath) in allFiles)
            {
                var destFilePath = CombineSmbPath(destinationPath, relativePath);
                var fullDestFilePath = CombineSmbPath(_smbPath, destFilePath);
                var destDir = Path.GetDirectoryName(fullDestFilePath);
                
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                var sourceFilePath = CombineSmbPath(sourcePath, relativePath);
                var fullSourceFilePath = CombineSmbPath(_smbPath, sourceFilePath);
                
                long fileSize = file.Length;
                long fileBytesRead = 0;
                
                // Kopiuj plik z postępem
                using (var sourceStream = new FileStream(fullSourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var destStream = new FileStream(fullDestFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920]; // 80KB buffer
                        int bytesRead;

                        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await destStream.WriteAsync(buffer, 0, bytesRead);
                            fileBytesRead += bytesRead;
                            long previousMovedSize = movedSize;
                            movedSize = previousMovedSize + bytesRead;
                            progress?.Report(movedSize);
                        }
                    }
                }

                // Usuń oryginalny plik
                File.Delete(fullSourceFilePath);
            }

            // Usuń oryginalny folder (teraz powinien być pusty)
            try
            {
                Directory.Delete(fullSourcePath, true);
            }
            catch
            {
                // Jeśli nie można usunąć, spróbuj usunąć tylko puste foldery
                DeleteEmptyDirectories(fullSourcePath);
            }

            _logger.LogInformation($"Folder przeniesiony: {sourcePath} -> {destinationPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Błąd podczas przenoszenia folderu: {sourcePath} -> {destinationPath}");
            throw;
        }
    }

    private void DeleteEmptyDirectories(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (var dir in Directory.GetDirectories(path))
            {
                DeleteEmptyDirectories(dir);
            }

            if (Directory.GetFiles(path).Length == 0 && Directory.GetDirectories(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Ignoruj błędy
        }
    }

    /// <summary>
    /// Łączy ścieżkę SMB (UNC) z podścieżką, obsługując poprawnie backslashe i forward slashe
    /// </summary>
    private string CombineSmbPath(string basePath, string subPath)
    {
        if (string.IsNullOrEmpty(subPath))
            return basePath;

        // Normalizuj separator w subPath (zamień forward slashe na backslashe dla Windows UNC)
        subPath = subPath.Replace('/', '\\');
        
        // Usuń wiodące backslashe z subPath
        subPath = subPath.TrimStart('\\');
        
        // Jeśli basePath kończy się backslashem, usuń go
        basePath = basePath.TrimEnd('\\');
        
        // Połącz ścieżki
        return $"{basePath}\\{subPath}";
    }
}



