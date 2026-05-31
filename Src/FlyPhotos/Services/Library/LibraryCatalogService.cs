using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FlyPhotos.Services.Library;

internal sealed class LibraryCatalogService
{
    private const string ReadyStatus = "Ready";
    private const string ErrorStatus = "Error";

    private static readonly Lazy<LibraryCatalogService> _instance = new(() => new LibraryCatalogService());

    private readonly string _dbPath;
    private readonly string _connectionString;

    public static LibraryCatalogService Instance => _instance.Value;

    private LibraryCatalogService()
    {
        _dbPath = PathResolver.GetLibraryDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        _connectionString = builder.ToString();
    }

    public string DatabasePath => _dbPath;

    public void Initialize()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS watched_folders (
                folder_path TEXT PRIMARY KEY,
                created_utc TEXT NOT NULL,
                last_scan_started_utc TEXT NULL,
                last_scan_completed_utc TEXT NULL,
                last_error TEXT NULL,
                item_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS assets (
                asset_id INTEGER PRIMARY KEY AUTOINCREMENT,
                watched_folder_path TEXT NOT NULL,
                file_path TEXT NOT NULL UNIQUE,
                folder_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                file_created_utc TEXT NULL,
                file_modified_utc TEXT NULL,
                file_size_bytes INTEGER NOT NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                date_taken_utc TEXT NULL,
                camera_make TEXT NULL,
                camera_model TEXT NULL,
                title TEXT NULL,
                caption TEXT NULL,
                rating INTEGER NULL,
                change_token TEXT NOT NULL,
                index_status TEXT NOT NULL,
                last_indexed_utc TEXT NULL,
                last_seen_utc TEXT NULL,
                last_error TEXT NULL,
                FOREIGN KEY (watched_folder_path) REFERENCES watched_folders(folder_path) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS asset_search (
                asset_id INTEGER PRIMARY KEY,
                file_name_key TEXT NOT NULL,
                folder_path_key TEXT NOT NULL,
                title_key TEXT NULL,
                caption_key TEXT NULL,
                FOREIGN KEY (asset_id) REFERENCES assets(asset_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_assets_watched_folder_path ON assets(watched_folder_path);
            CREATE INDEX IF NOT EXISTS idx_assets_file_name ON assets(file_name);
            CREATE INDEX IF NOT EXISTS idx_assets_folder_path ON assets(folder_path);
            CREATE INDEX IF NOT EXISTS idx_assets_date_taken_utc ON assets(date_taken_utc);
            CREATE INDEX IF NOT EXISTS idx_assets_rating ON assets(rating);
            CREATE INDEX IF NOT EXISTS idx_assets_change_token ON assets(change_token);
            CREATE INDEX IF NOT EXISTS idx_asset_search_file_name_key ON asset_search(file_name_key);
            CREATE INDEX IF NOT EXISTS idx_asset_search_folder_path_key ON asset_search(folder_path_key);
            CREATE INDEX IF NOT EXISTS idx_asset_search_title_key ON asset_search(title_key);
            CREATE INDEX IF NOT EXISTS idx_asset_search_caption_key ON asset_search(caption_key);
            """;
        command.ExecuteNonQuery();
    }

    public async Task SynchronizeWatchedFoldersAsync(IReadOnlyCollection<string> watchedFolders)
    {
        var normalizedFolders = watchedFolders
            .Select(NormalizeFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var connection = CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT folder_path FROM watched_folders;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(0));
        }

        foreach (var folder in normalizedFolders)
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO watched_folders (folder_path, created_utc)
                VALUES ($folderPath, $createdUtc)
                ON CONFLICT(folder_path) DO NOTHING;
                """;
            upsert.Parameters.AddWithValue("$folderPath", folder);
            upsert.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
            await upsert.ExecuteNonQueryAsync();
        }

        var removed = existing.Where(folder => !normalizedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var folder in removed)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM watched_folders WHERE folder_path = $folderPath;";
            delete.Parameters.AddWithValue("$folderPath", folder);
            await delete.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<int> GetIndexedAssetCountAsync()
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM assets WHERE index_status = $status;";
        command.Parameters.AddWithValue("$status", ReadyStatus);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<LibrarySearchResult>> SearchAsync(LibrarySearchQuery query)
    {
        var results = new List<LibrarySearchResult>();
        var normalizedText = NormalizeKey(query.Text);
        var likeText = $"%{EscapeLike(normalizedText)}%";
        var dateStart = query.DateFilter?.Date;
        var dateEnd = dateStart?.AddDays(1);
        var minimumRating = query.MinimumRating;

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                a.asset_id,
                a.file_path,
                a.file_name,
                a.folder_path,
                a.date_taken_utc,
                a.rating,
                a.title,
                a.caption
            FROM assets a
            INNER JOIN asset_search s ON s.asset_id = a.asset_id
            WHERE a.index_status = $readyStatus
              AND (
                    $searchText = ''
                    OR s.file_name_key LIKE $searchLike ESCAPE '\'
                    OR s.folder_path_key LIKE $searchLike ESCAPE '\'
                    OR IFNULL(s.title_key, '') LIKE $searchLike ESCAPE '\'
                    OR IFNULL(s.caption_key, '') LIKE $searchLike ESCAPE '\'
                  )
              AND (
                    $dateStart IS NULL
                    OR (
                        COALESCE(a.date_taken_utc, a.file_modified_utc) >= $dateStart
                        AND COALESCE(a.date_taken_utc, a.file_modified_utc) < $dateEnd
                    )
                  )
              AND (
                    $minimumRating IS NULL
                    OR COALESCE(a.rating, 0) >= $minimumRating
                  )
            ORDER BY COALESCE(a.date_taken_utc, a.file_modified_utc) DESC, a.file_name ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$readyStatus", ReadyStatus);
        command.Parameters.AddWithValue("$searchText", normalizedText);
        command.Parameters.AddWithValue("$searchLike", likeText);
        command.Parameters.AddWithValue("$dateStart", (object?)dateStart?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$dateEnd", (object?)dateEnd?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$minimumRating", (object?)minimumRating ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.Limit);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new LibrarySearchResult(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadDateTimeOffset(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    public async Task ApplyScanAsync(
        IReadOnlyList<IndexedWatchedFolder> scannedFolders,
        IReadOnlyList<IndexedAssetCandidate> scannedAssets)
    {
        await using var connection = CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync();

        var scannedFolderSet = scannedFolders
            .Select(folder => NormalizeFolderPath(folder.FolderPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingAssets = new List<(long AssetId, string FilePath, string ChangeToken)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT asset_id, file_path, change_token FROM assets;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existingAssets.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var existingByPath = existingAssets.ToDictionary(asset => asset.FilePath, StringComparer.OrdinalIgnoreCase);
        var existingByToken = existingAssets
            .GroupBy(asset => asset.ChangeToken, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Queue<(long AssetId, string FilePath, string ChangeToken)>(group),
                StringComparer.Ordinal);

        var handledAssetIds = new HashSet<long>();
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in scannedAssets)
        {
            scannedPaths.Add(asset.FilePath);
            if (existingByPath.TryGetValue(asset.FilePath, out var currentAsset))
            {
                handledAssetIds.Add(currentAsset.AssetId);
                await UpsertAssetAsync(connection, transaction, currentAsset.AssetId, asset);
                continue;
            }

            if (existingByToken.TryGetValue(asset.ChangeToken, out var queue))
            {
                while (queue.Count > 0)
                {
                    var candidate = queue.Dequeue();
                    if (handledAssetIds.Contains(candidate.AssetId))
                        continue;

                    handledAssetIds.Add(candidate.AssetId);
                    await UpsertAssetAsync(connection, transaction, candidate.AssetId, asset);
                    goto NextAsset;
                }
            }

            await UpsertAssetAsync(connection, transaction, null, asset);
        NextAsset:
            ;
        }

        foreach (var asset in existingAssets)
        {
            if (!handledAssetIds.Contains(asset.AssetId) && !scannedPaths.Contains(asset.FilePath))
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM assets WHERE asset_id = $assetId;";
                delete.Parameters.AddWithValue("$assetId", asset.AssetId);
                await delete.ExecuteNonQueryAsync();
            }
        }

        foreach (var folder in scannedFolders)
        {
            await using var updateFolder = connection.CreateCommand();
            updateFolder.Transaction = transaction;
            updateFolder.CommandText = """
                UPDATE watched_folders
                SET last_scan_started_utc = $startedUtc,
                    last_scan_completed_utc = $completedUtc,
                    last_error = $lastError,
                    item_count = $itemCount
                WHERE folder_path = $folderPath;
                """;
            updateFolder.Parameters.AddWithValue("$folderPath", NormalizeFolderPath(folder.FolderPath));
            updateFolder.Parameters.AddWithValue("$startedUtc", folder.ScanStartedUtc.ToString("O"));
            updateFolder.Parameters.AddWithValue("$completedUtc", (object?)folder.ScanCompletedUtc?.ToString("O") ?? DBNull.Value);
            updateFolder.Parameters.AddWithValue("$lastError", (object?)folder.ErrorMessage ?? DBNull.Value);
            updateFolder.Parameters.AddWithValue("$itemCount", folder.ItemCount);
            await updateFolder.ExecuteNonQueryAsync();
        }

        if (scannedFolderSet.Count == 0)
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM assets;";
            await clear.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task UpsertAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? assetId,
        IndexedAssetCandidate asset)
    {
        var indexedUtc = DateTimeOffset.UtcNow.ToString("O");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (assetId.HasValue)
        {
            command.CommandText = """
                UPDATE assets
                SET watched_folder_path = $watchedFolderPath,
                    file_path = $filePath,
                    folder_path = $folderPath,
                    file_name = $fileName,
                    file_created_utc = $fileCreatedUtc,
                    file_modified_utc = $fileModifiedUtc,
                    file_size_bytes = $fileSizeBytes,
                    width = $width,
                    height = $height,
                    date_taken_utc = $dateTakenUtc,
                    camera_make = $cameraMake,
                    camera_model = $cameraModel,
                    title = $title,
                    caption = $caption,
                    rating = $rating,
                    change_token = $changeToken,
                    index_status = $indexStatus,
                    last_indexed_utc = $lastIndexedUtc,
                    last_seen_utc = $lastSeenUtc,
                    last_error = $lastError
                WHERE asset_id = $assetId;
                """;
            command.Parameters.AddWithValue("$assetId", assetId.Value);
        }
        else
        {
            command.CommandText = """
                INSERT INTO assets (
                    watched_folder_path,
                    file_path,
                    folder_path,
                    file_name,
                    file_created_utc,
                    file_modified_utc,
                    file_size_bytes,
                    width,
                    height,
                    date_taken_utc,
                    camera_make,
                    camera_model,
                    title,
                    caption,
                    rating,
                    change_token,
                    index_status,
                    last_indexed_utc,
                    last_seen_utc,
                    last_error
                )
                VALUES (
                    $watchedFolderPath,
                    $filePath,
                    $folderPath,
                    $fileName,
                    $fileCreatedUtc,
                    $fileModifiedUtc,
                    $fileSizeBytes,
                    $width,
                    $height,
                    $dateTakenUtc,
                    $cameraMake,
                    $cameraModel,
                    $title,
                    $caption,
                    $rating,
                    $changeToken,
                    $indexStatus,
                    $lastIndexedUtc,
                    $lastSeenUtc,
                    $lastError
                );
                SELECT last_insert_rowid();
                """;
        }

        command.Parameters.AddWithValue("$watchedFolderPath", asset.WatchedFolderPath);
        command.Parameters.AddWithValue("$filePath", asset.FilePath);
        command.Parameters.AddWithValue("$folderPath", asset.FolderPath);
        command.Parameters.AddWithValue("$fileName", asset.FileName);
        command.Parameters.AddWithValue("$fileCreatedUtc", asset.FileCreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$fileModifiedUtc", asset.FileModifiedUtc.ToString("O"));
        command.Parameters.AddWithValue("$fileSizeBytes", asset.FileSizeBytes);
        command.Parameters.AddWithValue("$width", asset.Width);
        command.Parameters.AddWithValue("$height", asset.Height);
        command.Parameters.AddWithValue("$dateTakenUtc", (object?)asset.DateTakenUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$cameraMake", (object?)asset.CameraMake ?? DBNull.Value);
        command.Parameters.AddWithValue("$cameraModel", (object?)asset.CameraModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)asset.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$caption", (object?)asset.Caption ?? DBNull.Value);
        command.Parameters.AddWithValue("$rating", (object?)asset.Rating ?? DBNull.Value);
        command.Parameters.AddWithValue("$changeToken", asset.ChangeToken);
        command.Parameters.AddWithValue("$indexStatus", asset.IndexStatus);
        command.Parameters.AddWithValue("$lastIndexedUtc", indexedUtc);
        command.Parameters.AddWithValue("$lastSeenUtc", indexedUtc);
        command.Parameters.AddWithValue("$lastError", (object?)asset.LastError ?? DBNull.Value);

        long finalAssetId;
        if (assetId.HasValue)
        {
            await command.ExecuteNonQueryAsync();
            finalAssetId = assetId.Value;
        }
        else
        {
            finalAssetId = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        await using var search = connection.CreateCommand();
        search.Transaction = transaction;
        search.CommandText = """
            INSERT INTO asset_search (asset_id, file_name_key, folder_path_key, title_key, caption_key)
            VALUES ($assetId, $fileNameKey, $folderPathKey, $titleKey, $captionKey)
            ON CONFLICT(asset_id) DO UPDATE SET
                file_name_key = excluded.file_name_key,
                folder_path_key = excluded.folder_path_key,
                title_key = excluded.title_key,
                caption_key = excluded.caption_key;
            """;
        search.Parameters.AddWithValue("$assetId", finalAssetId);
        search.Parameters.AddWithValue("$fileNameKey", NormalizeKey(asset.FileName));
        search.Parameters.AddWithValue("$folderPathKey", NormalizeKey(asset.FolderPath));
        search.Parameters.AddWithValue("$titleKey", (object?)NormalizeKey(asset.Title) ?? DBNull.Value);
        search.Parameters.AddWithValue("$captionKey", (object?)NormalizeKey(asset.Caption) ?? DBNull.Value);
        await search.ExecuteNonQueryAsync();
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var raw = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        return Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}
