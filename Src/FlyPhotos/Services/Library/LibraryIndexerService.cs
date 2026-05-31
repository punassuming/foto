using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Infra.Configuration;
using Windows.Storage;

namespace FlyPhotos.Services.Library;

internal sealed class LibraryIndexerService
{
    private static readonly Lazy<LibraryIndexerService> _instance = new(() => new LibraryIndexerService());
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _scanRequested = new(0);
    private readonly object _statusGate = new();

    private Task? _backgroundTask;
    private bool _started;
    private LibraryIndexingSnapshot _snapshot = new(false, "Idle - add watched folders in Settings.", null, null);

    public static LibraryIndexerService Instance => _instance.Value;

    public event EventHandler? StatusChanged;

    private LibraryIndexerService()
    {
    }

    public void Start()
    {
        lock (_statusGate)
        {
            if (_started)
                return;

            _started = true;
        }

        LibraryCatalogService.Instance.Initialize();
        _backgroundTask = Task.Run(() => BackgroundLoopAsync(_cts.Token));
        RequestScan();
    }

    public LibraryIndexingSnapshot GetStatusSnapshot()
    {
        lock (_statusGate)
        {
            return _snapshot with { };
        }
    }

    public async Task RefreshWatchedFoldersAsync()
    {
        LibraryCatalogService.Instance.Initialize();
        await LibraryCatalogService.Instance.SynchronizeWatchedFoldersAsync(AppConfig.Settings.WatchedFolders);
        RequestScan();
        PublishStatus(isRunning: false, statusText: "Library settings updated.");
    }

    private async Task BackgroundLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunScanAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishStatus(false, "Library indexing failed.", errorMessage: ex.Message);
            }

            try
            {
                await _scanRequested.WaitAsync(PollInterval, cancellationToken);
                while (_scanRequested.CurrentCount > 0)
                    await _scanRequested.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunScanAsync(CancellationToken cancellationToken)
    {
        var watchedFolders = AppConfig.Settings.WatchedFolders
            .Select(NormalizeFolderPath)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await LibraryCatalogService.Instance.SynchronizeWatchedFoldersAsync(watchedFolders);

        if (watchedFolders.Count == 0)
        {
            await LibraryCatalogService.Instance.ApplyScanAsync([], []);
            PublishStatus(false, "Idle - add watched folders in Settings.");
            return;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        PublishStatus(true, $"Indexing {watchedFolders.Count} watched folder(s)...");

        var scannedFolders = new List<IndexedWatchedFolder>();
        var scannedAssets = new List<IndexedAssetCandidate>();

        foreach (var watchedFolder in watchedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishStatus(true, $"Scanning {watchedFolder}");
            var folderScanStartedUtc = DateTimeOffset.UtcNow;
            string? folderError = null;
            var folderAssetCount = 0;

            try
            {
                if (!Directory.Exists(watchedFolder))
                {
                    folderError = "Folder not found.";
                }
                else
                {
                    foreach (var filePath in EnumeratePhotoFiles(watchedFolder))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var metadata = await BuildAssetCandidateAsync(watchedFolder, filePath);
                        scannedAssets.Add(metadata);
                        folderAssetCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                folderError = ex.Message;
            }

            scannedFolders.Add(new IndexedWatchedFolder(
                watchedFolder,
                folderAssetCount,
                folderScanStartedUtc,
                DateTimeOffset.UtcNow,
                folderError));
        }

        await LibraryCatalogService.Instance.ApplyScanAsync(scannedFolders, scannedAssets);
        PublishStatus(false, $"Indexed {scannedAssets.Count} photo(s).", DateTimeOffset.UtcNow);
    }

    private void RequestScan()
    {
        try
        {
            _scanRequested.Release();
        }
        catch (SemaphoreFullException)
        {
            // Duplicate refresh requests are intentionally coalesced into a single pending scan.
        }
    }

    private void PublishStatus(
        bool isRunning,
        string statusText,
        DateTimeOffset? lastCompletedUtc = null,
        string? errorMessage = null)
    {
        lock (_statusGate)
        {
            _snapshot = new LibraryIndexingSnapshot(
                isRunning,
                statusText,
                lastCompletedUtc ?? _snapshot.LastCompletedUtc,
                errorMessage);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<string> EnumeratePhotoFiles(string watchedFolder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System
        };

        foreach (var filePath in Directory.EnumerateFiles(watchedFolder, "*", options))
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(extension))
                continue;

            if (CodecDiscovery.SupportedExtensions.Contains(extension.ToLowerInvariant()))
                yield return Path.GetFullPath(filePath);
        }
    }

    private static async Task<IndexedAssetCandidate> BuildAssetCandidateAsync(string watchedFolder, string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var title = default(string);
        var caption = default(string);
        var cameraMake = default(string);
        var cameraModel = default(string);
        var width = 0;
        var height = 0;
        DateTimeOffset? dateTakenUtc = null;
        int? rating = null;
        string? lastError = null;
        var indexStatus = "Ready";

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            var imageProperties = await storageFile.Properties.GetImagePropertiesAsync();
            width = (int)imageProperties.Width;
            height = (int)imageProperties.Height;
            rating = imageProperties.Rating > 0 ? (int)imageProperties.Rating : null;
            if (imageProperties.DateTaken != DateTimeOffset.MinValue)
                dateTakenUtc = imageProperties.DateTaken.ToUniversalTime();

            var extraProperties = await storageFile.Properties.RetrievePropertiesAsync([
                "System.Title",
                "System.Comment",
                "System.Photo.CameraManufacturer",
                "System.Photo.CameraModel"
            ]);

            title = GetStringProperty(extraProperties, "System.Title");
            caption = GetStringProperty(extraProperties, "System.Comment");
            cameraMake = GetStringProperty(extraProperties, "System.Photo.CameraManufacturer");
            cameraModel = GetStringProperty(extraProperties, "System.Photo.CameraModel");
        }
        catch (Exception ex)
        {
            indexStatus = "Error";
            lastError = ex.Message;
        }

        return new IndexedAssetCandidate(
            NormalizeFolderPath(watchedFolder),
            Path.GetFullPath(filePath),
            NormalizeFolderPath(Path.GetDirectoryName(filePath) ?? watchedFolder),
            Path.GetFileName(filePath),
            fileInfo.CreationTimeUtc,
            fileInfo.LastWriteTimeUtc,
            fileInfo.Length,
            width,
            height,
            dateTakenUtc,
            cameraMake,
            cameraModel,
            title,
            caption,
            rating,
            $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}",
            indexStatus,
            lastError);
    }

    private static string? GetStringProperty(IDictionary<string, object> properties, string key)
    {
        return properties.TryGetValue(key, out var value)
            ? value as string
            : null;
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        return Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
