using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.GuestPass.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GuestPass.Storage;

/// <summary>
/// JSON-backed persistent store for share-link records.
/// </summary>
public sealed class ShareLinkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _path;
    private readonly string _directory;
    private readonly ILogger<ShareLinkStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="ShareLinkStore"/> class.</summary>
    public ShareLinkStore(IApplicationPaths applicationPaths, ILogger<ShareLinkStore> logger)
    {
        _directory = Path.Combine(applicationPaths.DataPath, "guestpass");
        _path = Path.Combine(_directory, "guestpass.json");
        _logger = logger;
    }

    /// <summary>Lists all persisted share links.</summary>
    public async Task<IReadOnlyList<ShareLinkRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets a share link by token hash.</summary>
    public async Task<ShareLinkRecord?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return records.FirstOrDefault(record =>
                string.Equals(record.TokenHash, tokenHash, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets a share link by id.</summary>
    public async Task<ShareLinkRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return records.FirstOrDefault(record => record.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the legacy <c>ShareUrl</c> field from a store written by v0.2.2 or earlier.
    /// That field held the full share URL including the raw token in its query string, so a
    /// store carried redeemable credentials alongside their own hashes. Deserialization
    /// already drops the unknown property, so loading and saving once is enough to rewrite
    /// the file without it. Runs at startup and is a no-op on an already-clean store.
    /// </summary>
    /// <returns>The number of records whose stored URL was discarded.</returns>
    public async Task<int> ScrubLegacyShareUrlsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return 0;
            }

            string raw;
            try
            {
                raw = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GuestPass: could not read the record store to scrub legacy share URLs.");
                return 0;
            }

            // Cheap textual probe: the property is gone from the model, so it cannot be
            // observed after deserialization.
            if (raw.IndexOf("\"ShareUrl\"", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return 0;
            }

            var records = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            await SortAndSaveUnlockedAsync(records, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "GuestPass: removed the legacy ShareUrl field from {Count} stored record(s). Those URLs contained raw share tokens; existing links keep working, but they can no longer be re-read from disk.",
                records.Count);
            return records.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Inserts or replaces a share-link record.</summary>
    public async Task UpsertAsync(ShareLinkRecord record, CancellationToken cancellationToken = default)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (record.Id == Guid.Empty)
            {
                record.Id = Guid.NewGuid();
            }

            var index = records.FindIndex(existing => existing.Id == record.Id);
            if (index >= 0)
            {
                records[index] = record;
            }
            else
            {
                records.Add(record);
            }

            await SortAndSaveUnlockedAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Updates an existing share-link record.</summary>
    public async Task UpdateAsync(ShareLinkRecord record, CancellationToken cancellationToken = default)
    {
        await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a share-link record by id.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            records.RemoveAll(record => record.Id == id);
            await SortAndSaveUnlockedAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<ShareLinkRecord>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new List<ShareLinkRecord>();
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            return (await JsonSerializer.DeserializeAsync<List<ShareLinkRecord>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false)) ?? new List<ShareLinkRecord>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GuestPass: could not read the record store.");
            return new List<ShareLinkRecord>();
        }
    }

    private async Task SortAndSaveUnlockedAsync(List<ShareLinkRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);

        var ordered = records
            .OrderByDescending(record => record.CreatedAtUtc)
            .ThenBy(record => record.Id)
            .ToList();

        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, ordered, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GuestPass: could not write the record store.");
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }

            throw;
        }
    }
}
