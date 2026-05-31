using System;

namespace FlyPhotos.Services.Library;

internal sealed record LibrarySearchQuery(
    string? Text,
    DateTimeOffset? DateFilter,
    int? MinimumRating,
    int Limit = 100);

internal sealed record LibrarySearchResult(
    long AssetId,
    string FilePath,
    string FileName,
    string FolderPath,
    DateTimeOffset? DateTakenUtc,
    int? Rating,
    string? Title,
    string? Caption);

internal sealed record LibraryIndexingSnapshot(
    bool IsRunning,
    string StatusText,
    DateTimeOffset? LastCompletedUtc,
    string? ErrorMessage);

internal sealed record IndexedWatchedFolder(
    string FolderPath,
    int ItemCount,
    DateTimeOffset ScanStartedUtc,
    DateTimeOffset? ScanCompletedUtc,
    string? ErrorMessage);

internal sealed record IndexedAssetCandidate(
    string WatchedFolderPath,
    string FilePath,
    string FolderPath,
    string FileName,
    DateTimeOffset FileCreatedUtc,
    DateTimeOffset FileModifiedUtc,
    long FileSizeBytes,
    int Width,
    int Height,
    DateTimeOffset? DateTakenUtc,
    string? CameraMake,
    string? CameraModel,
    string? Title,
    string? Caption,
    int? Rating,
    string ChangeToken,
    string IndexStatus,
    string? LastError);
