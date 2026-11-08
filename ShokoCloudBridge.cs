#pragma warning disable CS0618, CS1701
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Quartz;
using Shoko.Models.MediaInfo;
using Shoko.Models.Server;
using Shoko.Plugin.Abstractions;
using Shoko.Server.API;
using Shoko.Server.API.Annotations;
using Shoko.Server.Models;
using Shoko.Server.Repositories;
using Shoko.Server.Scheduling;
using Shoko.Server.Scheduling.Jobs.Shoko;
using Shoko.Server.Server;
using Shoko.Server.Services;
using Shoko.Server.Utilities.MediaInfoLib;

namespace ShokoCloudBridge;

public class Plugin : IPlugin, IPluginServiceRegistration
{
    public string Name => "Shoko Cloud Bridge";

    public void Load()
    {
    }

    public void OnSettingsLoaded(IPluginSettings settings)
    {
    }

    public void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddSingleton<CloudBridgeService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<CloudBridgeService>());
        serviceCollection.AddTransient<CloudBridgeController>();
        serviceCollection.AddMvc().AddApplicationPart(typeof(Plugin).Assembly).AddControllersAsServices();
    }
}

public class CloudFolderConfig
{
    [JsonProperty("name")]
    public string Name { get; set; } = "Cloud";

    [JsonProperty("folder_path")]
    public string FolderPath { get; set; } = string.Empty;

    [JsonProperty("index_file_name")]
    public string IndexFileName { get; set; } = "_shoko_cloud_index.json";

    [JsonProperty("meta_file_name")]
    public string MetaFileName { get; set; } = "_shoko_cloud_index.meta.json";

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;
}

public class CloudBridgeConfig
{
    [JsonProperty("poll_interval_seconds")]
    public int PollIntervalSeconds { get; set; } = 60;

    [JsonProperty("block_local_hashing")]
    public bool BlockLocalHashing { get; set; } = true;

    [JsonProperty("auto_remove_deleted_files")]
    public bool AutoRemoveDeletedFiles { get; set; } = true;

    [JsonProperty("cloud_folders")]
    public List<CloudFolderConfig> CloudFolders { get; set; } = new();
}

public class FolderSyncState
{
    [JsonProperty("folder_path")]
    public string FolderPath { get; set; } = string.Empty;

    [JsonProperty("last_synced_updated_at")]
    public string? LastSyncedUpdatedAt { get; set; }

    [JsonProperty("last_synced_sha1")]
    public string? LastSyncedSha1 { get; set; }

    [JsonProperty("last_synced_index_size")]
    public long LastSyncedIndexSize { get; set; } = -1;

    [JsonProperty("last_synced_index_mtime_utc")]
    public DateTime? LastSyncedIndexMtimeUtc { get; set; }

    [JsonProperty("last_known_file_count")]
    public int LastKnownFileCount { get; set; }

    [JsonProperty("last_sync_utc")]
    public DateTime? LastSyncUtc { get; set; }

    [JsonProperty("last_sync_added_or_updated")]
    public int LastSyncAddedOrUpdated { get; set; }

    [JsonProperty("last_sync_queued_for_anidb")]
    public int LastSyncQueuedForAniDB { get; set; }
}

public class CloudBridgePersistentState
{
    [JsonProperty("folders")]
    public Dictionary<string, FolderSyncState> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class CloudIndexMeta
{
    [JsonProperty("version")]
    public int Version { get; set; } = 1;

    [JsonProperty("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonProperty("file_count")]
    public int FileCount { get; set; }

    [JsonProperty("index_size")]
    public long IndexSize { get; set; }

    [JsonProperty("index_sha1")]
    public string? IndexSha1 { get; set; }
}

public class CloudIndexFileEntry
{
    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("mtime")]
    public double Mtime { get; set; }

    [JsonProperty("ed2k")]
    public string Ed2k { get; set; } = string.Empty;

    [JsonProperty("crc32")]
    public string Crc32 { get; set; } = string.Empty;

    [JsonProperty("md5")]
    public string Md5 { get; set; } = string.Empty;

    [JsonProperty("sha1")]
    public string Sha1 { get; set; } = string.Empty;

    [JsonProperty("mediainfo")]
    public JObject? MediaInfo { get; set; }
}

public class CloudIndexManifest
{
    [JsonProperty("version")]
    public int Version { get; set; } = 1;

    [JsonProperty("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonProperty("files")]
    public Dictionary<string, CloudIndexFileEntry> Files { get; set; } = new();
}

public class CloudFolderStatusDto
{
    public string Name { get; set; } = string.Empty;
    public string CloudFolderPath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int? ImportFolderId { get; set; }
    public string IndexFilePath { get; set; } = string.Empty;
    public string MetaFilePath { get; set; } = string.Empty;
    public bool IndexFileExists { get; set; }
    public bool MetaFileExists { get; set; }
    public string? MetaUpdatedAt { get; set; }
    public string? MetaSha1 { get; set; }
    public long? MetaIndexSize { get; set; }
    public DateTime? IndexLastModifiedUtc { get; set; }
    public int TotalFilesInIndex { get; set; }
    public int TotalPlacesInShoko { get; set; }
    public int TotalMatchedOnAniDB { get; set; }
    public int TotalUnrecognizedInShoko { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public int LastSyncAddedOrUpdated { get; set; }
    public int LastSyncQueuedForAniDB { get; set; }
}

public class CloudBridgeStatus
{
    public string CloudFolderPath { get; set; } = string.Empty;
    public string IndexFilePath { get; set; } = string.Empty;
    public string MetaFilePath { get; set; } = string.Empty;
    public int? ImportFolderId { get; set; }
    public bool IndexFileExists { get; set; }
    public bool MetaFileExists { get; set; }
    public string? MetaUpdatedAt { get; set; }
    public string? MetaSha1 { get; set; }
    public DateTime? IndexLastModifiedUtc { get; set; }
    public int TotalFilesInIndex { get; set; }
    public int TotalPlacesInShoko { get; set; }
    public int TotalMatchedOnAniDB { get; set; }
    public int TotalUnrecognizedInShoko { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public int LastSyncAddedOrUpdated { get; set; }
    public int LastSyncQueuedForAniDB { get; set; }
    public int VetoedLocalGDriveHashJobs { get; set; }
    public int IndexFullReadsCount { get; set; }
    public int LightweightMetaChecksCount { get; set; }
    public string? LastError { get; set; }
    public CloudBridgeConfig Config { get; set; } = new();
    public List<CloudFolderStatusDto> Folders { get; set; } = new();
}

public class ShokoImportFolderDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsWatched { get; set; }
}

public class CloudHashGuardListener : ITriggerListener
{
    private readonly ILogger _logger;
    private readonly CloudBridgeService _service;

    public CloudHashGuardListener(ILogger logger, CloudBridgeService service)
    {
        _logger = logger;
        _service = service;
    }

    public string Name => "ShokoCloudBridge.CloudHashGuardListener";

    public ValueTask TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (context.JobDetail.JobType == typeof(HashFileJob))
            {
                var filePath = context.MergedJobDataMap.GetString("FilePath") ?? string.Empty;
                if (_service.ShouldBlockLocalHash(filePath))
                {
                    _service.IncrementVetoedCount();
                    _logger.LogWarning(
                        "[ShokoCloudBridge] VETOED local HashFileJob on cloud file '{FilePath}' to prevent downloading from cloud storage. Use the Cloud Indexer instead!",
                        filePath
                    );
                    _ = context.Scheduler.DeleteJob(context.JobDetail.Key, cancellationToken);
                    return new ValueTask<bool>(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ShokoCloudBridge] Error inside VetoJobExecution");
        }

        return new ValueTask<bool>(false);
    }

    public ValueTask TriggerMisfired(ITrigger trigger, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask TriggerComplete(ITrigger trigger, IJobExecutionContext context, SchedulerInstruction triggerInstructionCode, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public class CloudBridgeService : BackgroundService
{
    private readonly ILogger<CloudBridgeService> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly VideoLocal_PlaceService _vlPlaceService;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _configLock = new();

    private readonly string _pluginDir;
    private readonly string _configPath;
    private readonly string _statePath;

    private CloudBridgeConfig _config = new();
    private CloudBridgePersistentState _state = new();

    private int _vetoedCount;
    private int _indexFullReadsCount;
    private int _lightweightMetaChecksCount;
    private string? _lastError;
    private bool _guardRegistered;

    public CloudBridgeService(
        ILogger<CloudBridgeService> logger,
        ISchedulerFactory schedulerFactory,
        VideoLocal_PlaceService vlPlaceService)
    {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _vlPlaceService = vlPlaceService;

        var asmLoc = typeof(Plugin).Assembly.Location;
        _pluginDir = !string.IsNullOrEmpty(asmLoc)
            ? (Path.GetDirectoryName(asmLoc) ?? "/home/shoko/.shoko/Shoko.CLI/plugins")
            : "/home/shoko/.shoko/Shoko.CLI/plugins";
        _configPath = Path.Combine(_pluginDir, "ShokoCloudBridge.config.json");
        _statePath = Path.Combine(_pluginDir, "ShokoCloudBridge.state.json");

        LoadConfigAndState();
    }

    public void IncrementVetoedCount() => Interlocked.Increment(ref _vetoedCount);

    private static string NormalizeFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var norm = path.Trim().Replace('\\', '/');
        if (!norm.EndsWith("/"))
            norm += "/";
        return norm;
    }

    private void LoadConfigAndState()
    {
        lock (_configLock)
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var loaded = JsonConvert.DeserializeObject<CloudBridgeConfig>(json);
                    if (loaded != null)
                        _config = loaded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ShokoCloudBridge] Failed to load config from {Path}, using defaults", _configPath);
            }

            if (_config.CloudFolders == null)
            {
                _config.CloudFolders = new List<CloudFolderConfig>();
            }
            if (!File.Exists(_configPath))
            {
                SaveConfigInternal();
            }

            foreach (var f in _config.CloudFolders)
            {
                f.FolderPath = NormalizeFolderPath(f.FolderPath);
                if (string.IsNullOrWhiteSpace(f.IndexFileName))
                    f.IndexFileName = "_shoko_cloud_index.json";
                if (string.IsNullOrWhiteSpace(f.MetaFileName))
                    f.MetaFileName = "_shoko_cloud_index.meta.json";
            }

            try
            {
                if (File.Exists(_statePath))
                {
                    var sjson = File.ReadAllText(_statePath);
                    var loadedState = JsonConvert.DeserializeObject<CloudBridgePersistentState>(sjson);
                    if (loadedState?.Folders != null)
                        _state = loadedState;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ShokoCloudBridge] Failed to load state from {Path}", _statePath);
            }
        }
    }

    private void SaveConfigInternal()
    {
        try
        {
            Directory.CreateDirectory(_pluginDir);
            var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ShokoCloudBridge] Failed to save config to {Path}", _configPath);
        }
    }

    private void SaveStateInternal()
    {
        try
        {
            Directory.CreateDirectory(_pluginDir);
            var json = JsonConvert.SerializeObject(_state, Formatting.Indented);
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ShokoCloudBridge] Failed to save state to {Path}", _statePath);
        }
    }

    public CloudBridgeConfig GetConfig()
    {
        lock (_configLock)
        {
            return JsonConvert.DeserializeObject<CloudBridgeConfig>(JsonConvert.SerializeObject(_config)) ?? new CloudBridgeConfig();
        }
    }

    public CloudBridgeConfig UpdateConfig(CloudBridgeConfig newConfig)
    {
        lock (_configLock)
        {
            _config.PollIntervalSeconds = Math.Clamp(newConfig.PollIntervalSeconds, 10, 86400);
            _config.BlockLocalHashing = newConfig.BlockLocalHashing;
            _config.AutoRemoveDeletedFiles = newConfig.AutoRemoveDeletedFiles;
            _config.CloudFolders = (newConfig.CloudFolders ?? new List<CloudFolderConfig>())
                .Where(f => !string.IsNullOrWhiteSpace(f.FolderPath))
                .Select(f => new CloudFolderConfig
                {
                    Name = string.IsNullOrWhiteSpace(f.Name) ? "Cloud" : f.Name.Trim(),
                    FolderPath = NormalizeFolderPath(f.FolderPath),
                    IndexFileName = string.IsNullOrWhiteSpace(f.IndexFileName) ? "_shoko_cloud_index.json" : f.IndexFileName.Trim(),
                    MetaFileName = string.IsNullOrWhiteSpace(f.MetaFileName) ? "_shoko_cloud_index.meta.json" : f.MetaFileName.Trim(),
                    Enabled = f.Enabled
                })
                .ToList();

            SaveConfigInternal();
            foreach (var fc in _config.CloudFolders.Where(x => x.Enabled))
                EnsureImportFolder(fc);

            return GetConfig();
        }
    }

    public bool ShouldBlockLocalHash(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
        lock (_configLock)
        {
            if (!_config.BlockLocalHashing)
                return false;
            var normFile = filePath.Replace('\\', '/');
            foreach (var fc in _config.CloudFolders)
            {
                if (!string.IsNullOrWhiteSpace(fc.FolderPath) &&
                    normFile.StartsWith(fc.FolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ShokoCloudBridge] Waiting for Shoko ServerOnline...");
        while (!stoppingToken.IsCancellationRequested && !ServerState.Instance.ServerOnline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        _logger.LogInformation("[ShokoCloudBridge] Server is online. Initializing Cloud Bridge v2...");

        while (!stoppingToken.IsCancellationRequested)
        {
            int delaySec = 60;
            try
            {
                delaySec = Math.Clamp(GetConfig().PollIntervalSeconds, 10, 86400);
                await EnsureGuardAndFoldersAsync(stoppingToken);
                await CheckAndSyncIfChangedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "[ShokoCloudBridge] Error in background loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
        }
    }

    private async Task EnsureGuardAndFoldersAsync(CancellationToken token)
    {
        if (!_guardRegistered)
        {
            var scheduler = await _schedulerFactory.GetScheduler(token);
            scheduler.ListenerManager.AddTriggerListener(new CloudHashGuardListener(_logger, this));
            _guardRegistered = true;
            _logger.LogInformation("[ShokoCloudBridge] Registered CloudHashGuardListener.");
        }

        var cfg = GetConfig();
        foreach (var fc in cfg.CloudFolders.Where(f => f.Enabled))
        {
            EnsureImportFolder(fc);
        }
    }

    public SVR_ImportFolder? EnsureImportFolder(CloudFolderConfig fc)
    {
        var location = NormalizeFolderPath(fc.FolderPath);
        var folder = RepoFactory.ImportFolder.GetByImportLocation(location)
                     ?? RepoFactory.ImportFolder.GetByImportLocation(location.TrimEnd('/'));
        if (folder != null)
        {
            if (folder.IsWatched != 0 || folder.IsDropSource != 0 || folder.IsDropDestination != 0)
            {
                folder.IsWatched = 0;
                folder.IsDropSource = 0;
                folder.IsDropDestination = 0;
                RepoFactory.ImportFolder.Save(folder);
            }
            return folder;
        }

        if (!Directory.Exists(location))
        {
            _logger.LogWarning("[ShokoCloudBridge] Cloud folder {Path} is not mounted yet.", location);
            return null;
        }

        _logger.LogInformation("[ShokoCloudBridge] Creating ImportFolder '{Name}' at {Path}", fc.Name, location);
        return RepoFactory.ImportFolder.SaveImportFolder(new ImportFolder
        {
            ImportFolderName = fc.Name,
            ImportFolderLocation = location,
            ImportFolderType = 1,
            IsDropSource = 0,
            IsDropDestination = 0,
            IsWatched = 0
        });
    }

    private FolderSyncState GetOrCreateFolderState(string folderPath)
    {
        var norm = NormalizeFolderPath(folderPath);
        lock (_configLock)
        {
            if (!_state.Folders.TryGetValue(norm, out var st))
            {
                st = new FolderSyncState { FolderPath = norm };
                _state.Folders[norm] = st;
            }
            return st;
        }
    }

    private static CloudIndexMeta? TryReadMetaFile(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
                return null;
            var raw = File.ReadAllText(metaPath);
            return JsonConvert.DeserializeObject<CloudIndexMeta>(raw);
        }
        catch
        {
            return null;
        }
    }

    private async Task CheckAndSyncIfChangedAsync(CancellationToken token)
    {
        var cfg = GetConfig();
        foreach (var fc in cfg.CloudFolders.Where(f => f.Enabled))
        {
            var location = NormalizeFolderPath(fc.FolderPath);
            var indexPath = Path.Combine(location, fc.IndexFileName);
            var metaPath = Path.Combine(location, fc.MetaFileName);
            var st = GetOrCreateFolderState(location);

            Interlocked.Increment(ref _lightweightMetaChecksCount);

            // 1. Fast path: check tiny _shoko_cloud_index.meta.json (~160 bytes) first
            var meta = TryReadMetaFile(metaPath);
            if (meta != null)
            {
                if (meta.FileCount > 0 && st.LastKnownFileCount != meta.FileCount)
                {
                    st.LastKnownFileCount = meta.FileCount;
                    SaveStateInternal();
                }

                bool sha1Same = !string.IsNullOrEmpty(meta.IndexSha1) &&
                                string.Equals(meta.IndexSha1, st.LastSyncedSha1, StringComparison.OrdinalIgnoreCase);
                bool updatedSame = !string.IsNullOrEmpty(meta.UpdatedAt) &&
                                   string.Equals(meta.UpdatedAt, st.LastSyncedUpdatedAt, StringComparison.Ordinal);

                var folder = RepoFactory.ImportFolder.GetByImportLocation(location);
                int shokoCount = folder != null ? RepoFactory.VideoLocalPlace.GetByImportFolder(folder.ImportFolderID).Count : 0;

                if ((sha1Same || updatedSame) && (shokoCount > 0 || meta.FileCount == 0))
                {
                    continue;
                }

                _logger.LogInformation(
                    "[ShokoCloudBridge] Detected change in .meta.json for '{Folder}' (meta sha1={NewSha1}, prev sha1={OldSha1}). Triggering sync...",
                    location, meta.IndexSha1, st.LastSyncedSha1
                );
                await SyncFolderNowAsync(fc, forceAniDB: false, removeMissing: cfg.AutoRemoveDeletedFiles, token: token);
                continue;
            }

            // 2. Fallback if .meta.json does not exist yet: check FileInfo metadata (0 bytes content read)
            if (!File.Exists(indexPath))
                continue;

            var fi = new FileInfo(indexPath);
            if (st.LastSyncedIndexMtimeUtc.HasValue &&
                Math.Abs((fi.LastWriteTimeUtc - st.LastSyncedIndexMtimeUtc.Value).TotalSeconds) < 1.0 &&
                fi.Length == st.LastSyncedIndexSize)
            {
                continue;
            }

            _logger.LogInformation(
                "[ShokoCloudBridge] Detected change in index FileInfo for '{Folder}' (size={Size}). Triggering sync...",
                location, fi.Length
            );
            await SyncFolderNowAsync(fc, forceAniDB: false, removeMissing: cfg.AutoRemoveDeletedFiles, token: token);
        }
    }

    public async Task<CloudBridgeStatus> SyncNowAsync(bool forceAniDB = false, bool? removeMissing = null, CancellationToken token = default)
    {
        var cfg = GetConfig();
        bool rem = removeMissing ?? cfg.AutoRemoveDeletedFiles;
        foreach (var fc in cfg.CloudFolders.Where(f => f.Enabled))
        {
            await SyncFolderNowAsync(fc, forceAniDB, rem, token);
        }
        return GetStatus();
    }

    private async Task SyncFolderNowAsync(CloudFolderConfig fc, bool forceAniDB, bool removeMissing, CancellationToken token)
    {
        await _syncLock.WaitAsync(token);
        try
        {
            await EnsureGuardAndFoldersAsync(token);
            var location = NormalizeFolderPath(fc.FolderPath);
            var indexPath = Path.Combine(location, fc.IndexFileName);
            var metaPath = Path.Combine(location, fc.MetaFileName);

            var folder = EnsureImportFolder(fc);
            if (folder == null)
            {
                _lastError = $"Cloud folder '{location}' does not exist.";
                return;
            }

            if (!File.Exists(indexPath))
            {
                _lastError = $"Index file '{indexPath}' not found yet. Run the Cloud Indexer first.";
                return;
            }

            var fi = new FileInfo(indexPath);
            var rawBytes = await File.ReadAllBytesAsync(indexPath, token);
            Interlocked.Increment(ref _indexFullReadsCount);

            var rawSha1 = Convert.ToHexString(SHA1.HashData(rawBytes)).ToUpperInvariant();
            var rawJson = Encoding.UTF8.GetString(rawBytes);
            var manifest = ParseManifest(rawJson);

            var scheduler = await _schedulerFactory.GetScheduler(token);
            int addedOrUpdated = 0;
            int queuedForAniDB = 0;

            var validRelativePaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kvp in manifest.Files)
            {
                var relPath = kvp.Key.Replace('\\', '/').TrimStart('/');
                var entry = kvp.Value;
                if (string.IsNullOrWhiteSpace(relPath) || string.IsNullOrWhiteSpace(entry.Ed2k) || entry.Size <= 0)
                    continue;

                validRelativePaths.Add(relPath);

                var ed2k = entry.Ed2k.Trim().ToUpperInvariant();
                var crc32 = (entry.Crc32 ?? string.Empty).Trim().ToUpperInvariant();
                var md5 = (entry.Md5 ?? string.Empty).Trim().ToUpperInvariant();
                var sha1 = (entry.Sha1 ?? string.Empty).Trim().ToUpperInvariant();
                var fileName = Path.GetFileName(relPath);

                var vlocalplace = RepoFactory.VideoLocalPlace.GetByFilePathAndImportFolderID(relPath, folder.ImportFolderID);
                var vlocal = vlocalplace?.VideoLocal ?? RepoFactory.VideoLocal.GetByEd2k(ed2k);

                bool vlocalChanged = false;
                if (vlocal == null)
                {
                    vlocal = new SVR_VideoLocal
                    {
                        DateTimeCreated = DateTime.Now,
                        DateTimeUpdated = DateTime.Now,
                        FileName = fileName,
                        FileSize = entry.Size,
                        Hash = ed2k,
                        CRC32 = crc32,
                        MD5 = md5,
                        SHA1 = sha1,
                        HashSource = 1,
                        IsIgnored = false,
                        IsVariation = false
                    };
                    vlocalChanged = true;
                }
                else
                {
                    if (!string.Equals(vlocal.Hash, ed2k, StringComparison.OrdinalIgnoreCase))
                    {
                        vlocal.Hash = ed2k;
                        vlocalChanged = true;
                    }
                    if (!string.IsNullOrEmpty(crc32) && !string.Equals(vlocal.CRC32, crc32, StringComparison.OrdinalIgnoreCase))
                    {
                        vlocal.CRC32 = crc32;
                        vlocalChanged = true;
                    }
                    if (!string.IsNullOrEmpty(md5) && !string.Equals(vlocal.MD5, md5, StringComparison.OrdinalIgnoreCase))
                    {
                        vlocal.MD5 = md5;
                        vlocalChanged = true;
                    }
                    if (!string.IsNullOrEmpty(sha1) && !string.Equals(vlocal.SHA1, sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        vlocal.SHA1 = sha1;
                        vlocalChanged = true;
                    }
                    if (vlocal.FileSize != entry.Size)
                    {
                        vlocal.FileSize = entry.Size;
                        vlocalChanged = true;
                    }
                    if (string.IsNullOrEmpty(vlocal.FileName))
                    {
                        vlocal.FileName = fileName;
                        vlocalChanged = true;
                    }
                }

                if ((vlocal.MediaInfo == null || vlocal.MediaVersion < SVR_VideoLocal.MEDIA_VERSION) && entry.MediaInfo != null)
                {
                    var parsedMedia = TryParseMediaInfo(entry.MediaInfo.ToString(Formatting.None), relPath);
                    if (parsedMedia != null)
                    {
                        vlocal.MediaInfo = parsedMedia;
                        vlocal.MediaVersion = SVR_VideoLocal.MEDIA_VERSION;
                        vlocalChanged = true;
                    }
                }

                if (vlocalChanged || vlocal.VideoLocalID == 0)
                {
                    vlocal.DateTimeUpdated = DateTime.Now;
                    RepoFactory.VideoLocal.Save(vlocal, true);
                    addedOrUpdated++;
                }

                if (vlocalplace == null)
                {
                    vlocalplace = new SVR_VideoLocal_Place
                    {
                        FilePath = relPath,
                        ImportFolderID = folder.ImportFolderID,
                        ImportFolderType = folder.ImportFolderType,
                        VideoLocalID = vlocal.VideoLocalID
                    };
                    RepoFactory.VideoLocalPlace.Save(vlocalplace);
                    addedOrUpdated++;
                }
                else if (vlocalplace.VideoLocalID != vlocal.VideoLocalID)
                {
                    vlocalplace.VideoLocalID = vlocal.VideoLocalID;
                    RepoFactory.VideoLocalPlace.Save(vlocalplace);
                    addedOrUpdated++;
                }

                SaveFileNameHash(fileName, vlocal.FileSize, ed2k);

                bool hasXrefs = vlocal.EpisodeCrossReferences.Any(a => a.AnimeEpisode is not null && a.AnimeSeries is not null);
                if (!hasXrefs || forceAniDB)
                {
                    var vlId = vlocal.VideoLocalID;
                    await scheduler.StartJobNow<ProcessFileJob>(c =>
                    {
                        c.VideoLocalID = vlId;
                        c.ForceAniDB = forceAniDB;
                    });
                    queuedForAniDB++;
                }
            }

            if (removeMissing && validRelativePaths.Count > 0)
            {
                var existingPlaces = RepoFactory.VideoLocalPlace.GetByImportFolder(folder.ImportFolderID).ToList();
                foreach (var place in existingPlaces)
                {
                    var normPath = (place.FilePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
                    if (!validRelativePaths.Contains(normPath) && !File.Exists(place.FullServerPath))
                    {
                        _logger.LogInformation("[ShokoCloudBridge] Removing missing cloud file record: {Path}", normPath);
                        await _vlPlaceService.RemoveRecord(place);
                    }
                }
            }

            var meta = TryReadMetaFile(metaPath);
            var st = GetOrCreateFolderState(location);
            st.LastSyncedUpdatedAt = meta?.UpdatedAt ?? manifest.UpdatedAt;
            st.LastSyncedSha1 = meta?.IndexSha1 ?? rawSha1;
            st.LastSyncedIndexSize = fi.Length;
            st.LastSyncedIndexMtimeUtc = fi.LastWriteTimeUtc;
            st.LastKnownFileCount = manifest.Files.Count;
            st.LastSyncUtc = DateTime.UtcNow;
            st.LastSyncAddedOrUpdated = addedOrUpdated;
            st.LastSyncQueuedForAniDB = queuedForAniDB;
            SaveStateInternal();

            _lastError = null;

            _logger.LogInformation(
                "[ShokoCloudBridge] Sync completed for '{Folder}': {Total} in index, {Added} added/updated, {Queued} queued for AniDB.",
                location, manifest.Files.Count, addedOrUpdated, queuedForAniDB
            );
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "[ShokoCloudBridge] Sync failed for '{Folder}'", fc.FolderPath);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static void SaveFileNameHash(string fileName, long fileSize, string ed2k)
    {
        var existing = RepoFactory.FileNameHash.GetByFileNameAndSize(fileName, fileSize);
        if (existing is { Count: 1 } && string.Equals(existing[0].Hash, ed2k, StringComparison.OrdinalIgnoreCase))
            return;

        if (existing is { Count: > 1 })
            RepoFactory.FileNameHash.Delete(existing);

        var fnh = existing is { Count: 1 } ? existing[0] : new FileNameHash();
        fnh.FileName = fileName;
        fnh.FileSize = fileSize;
        fnh.Hash = ed2k;
        fnh.DateTimeUpdated = DateTime.Now;
        RepoFactory.FileNameHash.Save(fnh);
    }

    private MediaContainer? TryParseMediaInfo(string json, string filename)
    {
        try
        {
            var settings = new JsonSerializerSettings
            {
                Converters = new JsonConverter[]
                {
                    new StreamJsonConverter(),
                    new BooleanConverter(),
                    new StringEnumConverter(),
                    new Shoko.Server.Utilities.MediaInfoLib.DateTimeConverter { DateTimeFormat = "yyyy-MM-dd HH:mm:ss" },
                    new MultiIntConverter(),
                    new MenuBase64Converter()
                },
                Error = (_, e) => { e.ErrorContext.Handled = true; }
            };

            var m = JsonConvert.DeserializeObject<MediaContainer>(json, settings);
            if (m?.media?.track == null)
                return null;

            foreach (var track in m.media.track)
            {
                if (string.IsNullOrEmpty(track?.Language))
                    continue;
                var languages = MediaInfoUtils.GetLanguageMapping(track.Language);
                if (languages != null)
                {
                    track.LanguageCode = languages.Item1;
                    track.LanguageName = languages.Item2;
                }
            }

            return m;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ShokoCloudBridge] Could not parse MediaInfo JSON for {File}", filename);
            return null;
        }
    }

    private static CloudIndexManifest ParseManifest(string rawJson)
    {
        var jObj = JObject.Parse(rawJson);
        if (jObj.TryGetValue("files", StringComparison.OrdinalIgnoreCase, out var filesToken) && filesToken is JObject)
        {
            return jObj.ToObject<CloudIndexManifest>() ?? new CloudIndexManifest();
        }

        var files = jObj.ToObject<Dictionary<string, CloudIndexFileEntry>>() ?? new Dictionary<string, CloudIndexFileEntry>();
        return new CloudIndexManifest { Version = 1, Files = files };
    }

    public List<ShokoImportFolderDto> GetAllShokoImportFolders()
    {
        return RepoFactory.ImportFolder.GetAll()
            .Select(f => new ShokoImportFolderDto
            {
                Id = f.ImportFolderID,
                Name = f.ImportFolderName,
                Path = NormalizeFolderPath(f.ImportFolderLocation),
                IsWatched = f.IsWatched != 0
            })
            .ToList();
    }

    public CloudBridgeStatus GetStatus()
    {
        var cfg = GetConfig();
        var folderDtos = new List<CloudFolderStatusDto>();

        foreach (var fc in cfg.CloudFolders)
        {
            var location = NormalizeFolderPath(fc.FolderPath);
            var indexPath = Path.Combine(location, fc.IndexFileName);
            var metaPath = Path.Combine(location, fc.MetaFileName);
            var st = GetOrCreateFolderState(location);

            var folder = RepoFactory.ImportFolder.GetByImportLocation(location)
                         ?? RepoFactory.ImportFolder.GetByImportLocation(location.TrimEnd('/'));
            var places = folder != null
                ? RepoFactory.VideoLocalPlace.GetByImportFolder(folder.ImportFolderID)
                : Array.Empty<SVR_VideoLocal_Place>();

            int matched = 0;
            int unrecognizedCount = 0;

            foreach (var p in places)
            {
                var vl = p.VideoLocal;
                if (vl != null && vl.EpisodeCrossReferences.Any(a => a.AnimeEpisode is not null && a.AnimeSeries is not null))
                {
                    matched++;
                }
                else if (vl != null)
                {
                    unrecognizedCount++;
                }
            }

            // Read ONLY tiny .meta.json (~160 bytes) if present; NEVER read the 1MB+ main index inside GetStatus()
            var meta = TryReadMetaFile(metaPath);
            bool indexExists = File.Exists(indexPath);
            DateTime? indexMtime = indexExists ? new FileInfo(indexPath).LastWriteTimeUtc : null;

            int indexCount = meta?.FileCount > 0 ? meta.FileCount : st.LastKnownFileCount;

            folderDtos.Add(new CloudFolderStatusDto
            {
                Name = fc.Name,
                CloudFolderPath = location,
                Enabled = fc.Enabled,
                ImportFolderId = folder?.ImportFolderID,
                IndexFilePath = indexPath,
                MetaFilePath = metaPath,
                IndexFileExists = indexExists,
                MetaFileExists = meta != null,
                MetaUpdatedAt = meta?.UpdatedAt ?? st.LastSyncedUpdatedAt,
                MetaSha1 = meta?.IndexSha1 ?? st.LastSyncedSha1,
                MetaIndexSize = meta?.IndexSize ?? (indexExists ? new FileInfo(indexPath).Length : null),
                IndexLastModifiedUtc = indexMtime,
                TotalFilesInIndex = indexCount,
                TotalPlacesInShoko = places.Count,
                TotalMatchedOnAniDB = matched,
                TotalUnrecognizedInShoko = unrecognizedCount,
                LastSyncUtc = st.LastSyncUtc,
                LastSyncAddedOrUpdated = st.LastSyncAddedOrUpdated,
                LastSyncQueuedForAniDB = st.LastSyncQueuedForAniDB
            });
        }

        var primary = folderDtos.FirstOrDefault() ?? new CloudFolderStatusDto();

        return new CloudBridgeStatus
        {
            CloudFolderPath = primary.CloudFolderPath,
            IndexFilePath = primary.IndexFilePath,
            MetaFilePath = primary.MetaFilePath,
            ImportFolderId = primary.ImportFolderId,
            IndexFileExists = primary.IndexFileExists,
            MetaFileExists = primary.MetaFileExists,
            MetaUpdatedAt = primary.MetaUpdatedAt,
            MetaSha1 = primary.MetaSha1,
            IndexLastModifiedUtc = primary.IndexLastModifiedUtc,
            TotalFilesInIndex = folderDtos.Sum(f => f.TotalFilesInIndex),
            TotalPlacesInShoko = folderDtos.Sum(f => f.TotalPlacesInShoko),
            TotalMatchedOnAniDB = folderDtos.Sum(f => f.TotalMatchedOnAniDB),
            TotalUnrecognizedInShoko = folderDtos.Sum(f => f.TotalUnrecognizedInShoko),
            LastSyncUtc = primary.LastSyncUtc,
            LastSyncAddedOrUpdated = folderDtos.Sum(f => f.LastSyncAddedOrUpdated),
            LastSyncQueuedForAniDB = folderDtos.Sum(f => f.LastSyncQueuedForAniDB),
            VetoedLocalGDriveHashJobs = _vetoedCount,
            IndexFullReadsCount = _indexFullReadsCount,
            LightweightMetaChecksCount = _lightweightMetaChecksCount,
            LastError = _lastError,
            Config = cfg,
            Folders = folderDtos
        };
    }
}

[ApiController]
[Route("/api/v{version:apiVersion}/[controller]")]
[Route("/api/[controller]")]
[ApiV3]
[ApiVersionNeutral]
[Authorize]
public class CloudBridgeController : ControllerBase
{
    private readonly CloudBridgeService _service;

    public CloudBridgeController(CloudBridgeService service)
    {
        _service = service;
    }

    [HttpGet("status")]
    public ActionResult<CloudBridgeStatus> GetStatus()
    {
        return Ok(_service.GetStatus());
    }

    [HttpGet("config")]
    public ActionResult<CloudBridgeConfig> GetConfig()
    {
        return Ok(_service.GetConfig());
    }

    [HttpPost("config")]
    public ActionResult<CloudBridgeStatus> UpdateConfig([FromBody] CloudBridgeConfig config)
    {
        _service.UpdateConfig(config);
        return Ok(_service.GetStatus());
    }

    [HttpGet("import-folders")]
    public ActionResult<List<ShokoImportFolderDto>> GetImportFolders()
    {
        return Ok(_service.GetAllShokoImportFolders());
    }

    [HttpPost("sync")]
    public async Task<ActionResult<CloudBridgeStatus>> SyncNow(
        [FromQuery] bool forceAniDB = false,
        [FromQuery] bool? removeMissing = null,
        CancellationToken cancellationToken = default)
    {
        var status = await _service.SyncNowAsync(forceAniDB, removeMissing, cancellationToken);
        return Ok(status);
    }

    [AllowAnonymous]
    [HttpGet("/CloudBridge")]
    [HttpGet("ui")]
    public ContentResult GetWebUi()
    {
        return Content(LoginShellHtml, "text/html; charset=utf-8");
    }

    [HttpGet("app")]
    public ActionResult GetAuthenticatedApp()
    {
        return Ok(new
        {
            html = DashboardTemplateHtml,
            js = DashboardScriptJs
        });
    }

    private const string DashboardTemplateHtml = @"
  <header>
    <div class=""title-wrap"">
      <h1>
        ☁️ Shoko Cloud Bridge
        <span id=""metaBadge"" class=""badge"">Checking...</span>
      </h1>
      <div class=""subtitle"">Sync pre-indexed cloud or remote folders (rclone / Google Drive / NAS) without downloading video files</div>
    </div>
    <div class=""btn-group"">
      <button class=""primary"" onclick=""window.cbSyncNow(false)"">🔄 Sync Now</button>
      <button onclick=""window.cbSyncNow(true)"">⚡ Force AniDB Recheck</button>
      <a class=""btn"" href=""/webui/"" target=""_blank"">Open Shoko WebUI ↗</a>
      <button onclick=""window.cbSignOut()"">Sign Out</button>
    </div>
  </header>

  <div id=""errorBox"" class=""error-box""></div>

  <div class=""grid"">
    <div class=""stat-card"">
      <div class=""stat-label"">Files in Cloud Index</div>
      <div class=""stat-val"" id=""stIndexCount"">—</div>
      <div class=""stat-sub"" id=""stMetaInfo"">—</div>
    </div>
    <div class=""stat-card"">
      <div class=""stat-label"">Imported in Shoko DB</div>
      <div class=""stat-val"" id=""stShokoPlaces"">—</div>
      <div class=""stat-sub"" id=""stLastSync"">—</div>
    </div>
    <div class=""stat-card"">
      <div class=""stat-label"">Matched on AniDB</div>
      <div class=""stat-val"" style=""color: var(--success)"" id=""stMatched"">—</div>
      <div class=""stat-sub"" id=""stUnrecSub"">—</div>
    </div>
    <div class=""stat-card"">
      <div class=""stat-label"">Bandwidth Saved (.meta.json)</div>
      <div class=""stat-val"" style=""color: var(--accent)"" id=""stMetaChecks"">—</div>
      <div class=""stat-sub"" id=""stFullReads"">—</div>
    </div>
    <div class=""stat-card"">
      <div class=""stat-label"">CloudHashGuard</div>
      <div class=""stat-val"" id=""stVetoed"">0</div>
      <div class=""stat-sub"">Blocked local hash jobs</div>
    </div>
  </div>

  <div class=""card"">
    <h2>
      <span>📁 Cloud & Remote Import Folders</span>
      <div style=""display:flex; gap:8px; align-items:center;"">
        <select id=""existingFolderSelect"" style=""width:260px;"">
          <option value="""">+ Pick from Shoko Import Folders...</option>
        </select>
        <button onclick=""window.cbAddCloudFolder()"">+ Add Folder</button>
      </div>
    </h2>
    <table>
      <thead>
        <tr>
          <th style=""width:60px;"">Enabled</th>
          <th style=""width:130px;"">Name</th>
          <th>Server Mount Path (any rclone / network / local folder)</th>
          <th style=""width:190px;"">Index File Name</th>
          <th style=""width:180px;"">Status</th>
          <th style=""width:50px;""></th>
        </tr>
      </thead>
      <tbody id=""foldersTableBody""></tbody>
    </table>
  </div>

  <div class=""card"">
    <h2>⚙️ Plugin Settings</h2>
    <div class=""form-row"">
      <div>
        <label style=""font-size:12px; color:var(--muted); display:block; margin-bottom:4px;"">Poll Interval for .meta.json (seconds)</label>
        <input type=""number"" id=""cfgPollInterval"" min=""10"" max=""86400"">
      </div>
    </div>
    <label class=""checkbox-row"">
      <input type=""checkbox"" id=""cfgBlockHashing"">
      <span><strong>Block Local Hashing (CloudHashGuard)</strong> — prevents Shoko from reading/downloading video files in cloud folders</span>
    </label>
    <label class=""checkbox-row"">
      <input type=""checkbox"" id=""cfgAutoRemove"">
      <span><strong>Auto-Remove Deleted Files</strong> — removes entries from Shoko when they are removed from the cloud index</span>
    </label>
    <div style=""margin-top: 14px;"">
      <button class=""primary"" onclick=""window.cbSaveConfig()"">💾 Save Settings</button>
    </div>
  </div>";

    private const string DashboardScriptJs = @"
(function() {
  let currentStatus = null;
  let shokoFolders = [];

  function showToast(msg) {
    const t = document.getElementById('toast');
    t.textContent = msg;
    t.style.display = 'block';
    setTimeout(() => { t.style.display = 'none'; }, 3500);
  }

  function norm(o) {
    if (!o || typeof o !== 'object') return o;
    if (Array.isArray(o)) return o.map(norm);
    const res = {};
    for (const k of Object.keys(o)) {
      const camel = k.charAt(0).toLowerCase() + k.slice(1);
      res[camel] = norm(o[k]);
      res[k] = res[camel];
    }
    return res;
  }

  async function refreshData() {
    try {
      const [stRes, ifRes] = await Promise.all([
        window.cbApiFetch('/api/CloudBridge/status'),
        window.cbApiFetch('/api/CloudBridge/import-folders')
      ]);
      currentStatus = norm(await stRes.json());
      shokoFolders = norm(await ifRes.json());
      render();
    } catch (e) {
      if (e.message !== 'Unauthorized') console.error(e);
    }
  }

  function render() {
    if (!currentStatus || !document.getElementById('stIndexCount')) return;
    const st = currentStatus;

    const errBox = document.getElementById('errorBox');
    if (st.lastError) {
      errBox.style.display = 'block';
      errBox.textContent = 'Error: ' + st.lastError;
    } else {
      errBox.style.display = 'none';
    }

    const badge = document.getElementById('metaBadge');
    if (st.metaFileExists) {
      badge.className = 'badge ok';
      badge.textContent = '✓ .meta.json active (~160 B)';
    } else if (st.indexFileExists) {
      badge.className = 'badge warn';
      badge.textContent = 'Index found (no .meta.json)';
    } else {
      badge.className = 'badge warn';
      badge.textContent = 'Waiting for index';
    }

    document.getElementById('stIndexCount').textContent = st.totalFilesInIndex;
    document.getElementById('stMetaInfo').textContent = st.metaSha1
      ? ('SHA1: ' + st.metaSha1.substring(0, 10) + '…')
      : 'Index metadata';

    document.getElementById('stShokoPlaces').textContent = st.totalPlacesInShoko;
    document.getElementById('stLastSync').textContent = st.lastSyncUtc
      ? ('Last sync: ' + new Date(st.lastSyncUtc).toLocaleString())
      : 'No sync required yet';

    document.getElementById('stMatched').textContent = st.totalMatchedOnAniDB + ' / ' + st.totalPlacesInShoko;
    document.getElementById('stUnrecSub').textContent = st.totalUnrecognizedInShoko > 0
      ? ('Unrecognized in AniDB: ' + st.totalUnrecognizedInShoko)
      : 'All files matched!';

    document.getElementById('stMetaChecks').textContent = st.lightweightMetaChecksCount + ' checks';
    document.getElementById('stFullReads').textContent = 'Full index file reads: ' + st.indexFullReadsCount;
    document.getElementById('stVetoed').textContent = st.vetoedLocalGDriveHashJobs;

    document.getElementById('cfgPollInterval').value = st.config.poll_interval_seconds || st.config.pollIntervalSeconds || 60;
    document.getElementById('cfgBlockHashing').checked = st.config.block_local_hashing ?? st.config.blockLocalHashing ?? true;
    document.getElementById('cfgAutoRemove').checked = st.config.auto_remove_deleted_files ?? st.config.autoRemoveDeletedFiles ?? true;

    const sel = document.getElementById('existingFolderSelect');
    sel.innerHTML = '<option value="""">+ Pick from Shoko Import Folders...</option>';
    for (const sf of shokoFolders) {
      const opt = document.createElement('option');
      opt.value = JSON.stringify(sf);
      opt.textContent = `${sf.name} (${sf.path})`;
      sel.appendChild(opt);
    }
    sel.onchange = () => {
      if (!sel.value) return;
      const sf = JSON.parse(sel.value);
      window.cbAddCloudFolder(sf.name, sf.path);
      sel.value = '';
    };

    const tbody = document.getElementById('foldersTableBody');
    tbody.innerHTML = '';
    const cfgFolders = st.config.cloud_folders || st.config.cloudFolders || [];
    cfgFolders.forEach((fc, idx) => {
      const stFolder = (st.folders || [])[idx] || {};
      const tr = document.createElement('tr');
      const statusText = stFolder.metaFileExists
        ? `<span class=""badge ok"">✓ .meta.json (${stFolder.totalPlacesInShoko} files)</span>`
        : (stFolder.indexFileExists
            ? `<span class=""badge warn"">Index (${stFolder.totalPlacesInShoko} files)</span>`
            : `<span class=""badge"">No index</span>`);

      tr.innerHTML = `
        <td><input type=""checkbox"" class=""fc-enabled"" ${fc.enabled !== false ? 'checked' : ''}></td>
        <td><input type=""text"" class=""fc-name""></td>
        <td><input type=""text"" class=""fc-path mono""></td>
        <td><input type=""text"" class=""fc-index mono""></td>
        <td>${statusText}</td>
        <td><button class=""danger"" onclick=""this.closest('tr').remove()"">✕</button></td>
      `;
      tr.querySelector('.fc-name').value = fc.name || 'Cloud';
      tr.querySelector('.fc-path').value = fc.folder_path || fc.folderPath || '';
      tr.querySelector('.fc-index').value = fc.index_file_name || fc.indexFileName || '_shoko_cloud_index.json';
      tbody.appendChild(tr);
    });
  }

  window.cbAddCloudFolder = function(name = 'Cloud', path = '/mnt/cloud/') {
    const tbody = document.getElementById('foldersTableBody');
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td><input type=""checkbox"" class=""fc-enabled"" checked></td>
      <td><input type=""text"" class=""fc-name""></td>
      <td><input type=""text"" class=""fc-path mono""></td>
      <td><input type=""text"" class=""fc-index mono""></td>
      <td><span class=""badge"">New</span></td>
      <td><button class=""danger"" onclick=""this.closest('tr').remove()"">✕</button></td>
    `;
    tr.querySelector('.fc-name').value = name;
    tr.querySelector('.fc-path').value = path;
    tr.querySelector('.fc-index').value = '_shoko_cloud_index.json';
    tbody.appendChild(tr);
  };

  window.cbSaveConfig = async function() {
    const rows = Array.from(document.querySelectorAll('#foldersTableBody tr'));
    const folders = rows.map(r => ({
      enabled: r.querySelector('.fc-enabled').checked,
      name: r.querySelector('.fc-name').value.trim(),
      folder_path: r.querySelector('.fc-path').value.trim(),
      index_file_name: r.querySelector('.fc-index').value.trim() || '_shoko_cloud_index.json',
      meta_file_name: '_shoko_cloud_index.meta.json'
    }));

    const payload = {
      poll_interval_seconds: parseInt(document.getElementById('cfgPollInterval').value, 10) || 60,
      block_local_hashing: document.getElementById('cfgBlockHashing').checked,
      auto_remove_deleted_files: document.getElementById('cfgAutoRemove').checked,
      cloud_folders: folders
    };

    const res = await window.cbApiFetch('/api/CloudBridge/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    currentStatus = norm(await res.json());
    render();
    showToast('✓ Settings saved!');
  };

  window.cbSyncNow = async function(forceAniDB) {
    showToast(forceAniDB ? '⏳ Syncing and queueing AniDB recheck...' : '⏳ Syncing cloud index...');
    const res = await window.cbApiFetch(`/api/CloudBridge/sync?forceAniDB=${forceAniDB}`, { method: 'POST' });
    currentStatus = norm(await res.json());
    render();
    showToast('✓ Sync completed!');
  };

  if (window.cbTimer) clearInterval(window.cbTimer);
  refreshData();
  window.cbTimer = setInterval(() => {
    if (document.getElementById('stIndexCount')) refreshData();
  }, 15000);
})();
";

    private const string LoginShellHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Shoko Cloud Bridge</title>
<style>
  :root {
    --bg: #0f141d;
    --panel: #171f2e;
    --panel-alt: #1e293b;
    --border: #2d3b52;
    --text: #e2e8f0;
    --muted: #94a3b8;
    --accent: #38bdf8;
    --accent-hover: #0ea5e9;
    --success: #22c55e;
    --warn: #f59e0b;
    --danger: #ef4444;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 24px;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Inter, sans-serif;
    background: var(--bg); color: var(--text);
    line-height: 1.5;
  }
  .container { max-width: 1120px; margin: 0 auto; }
  header {
    display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between;
    gap: 16px; margin-bottom: 24px; padding-bottom: 16px; border-bottom: 1px solid var(--border);
  }
  .title-wrap h1 { margin: 0; font-size: 22px; display: flex; align-items: center; gap: 10px; }
  .badge {
    font-size: 12px; padding: 3px 10px; border-radius: 999px; font-weight: 600;
    background: rgba(56, 189, 248, 0.15); color: var(--accent); border: 1px solid rgba(56, 189, 248, 0.3);
  }
  .badge.ok { background: rgba(34, 197, 94, 0.15); color: var(--success); border-color: rgba(34, 197, 94, 0.3); }
  .badge.warn { background: rgba(245, 158, 11, 0.15); color: var(--warn); border-color: rgba(245, 158, 11, 0.3); }
  .subtitle { color: var(--muted); font-size: 13px; margin-top: 4px; }
  .btn-group { display: flex; gap: 10px; flex-wrap: wrap; }
  button, .btn {
    background: var(--panel-alt); color: var(--text); border: 1px solid var(--border);
    padding: 8px 15px; border-radius: 8px; cursor: pointer; font-size: 13px; font-weight: 500;
    text-decoration: none; display: inline-flex; align-items: center; gap: 6px; transition: 0.15s;
  }
  button:hover, .btn:hover { border-color: var(--accent); background: #243247; }
  button.primary { background: var(--accent); color: #09131c; border-color: var(--accent); font-weight: 600; }
  button.primary:hover { background: var(--accent-hover); }
  button.danger { border-color: rgba(239,68,68,0.4); color: #fca5a5; }
  button.danger:hover { background: rgba(239,68,68,0.15); }
  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(175px, 1fr)); gap: 14px; margin-bottom: 24px; }
  .stat-card {
    background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 14px 16px;
  }
  .stat-label { color: var(--muted); font-size: 12px; margin-bottom: 4px; }
  .stat-val { font-size: 24px; font-weight: 700; }
  .stat-sub { font-size: 11px; color: var(--muted); margin-top: 4px; }
  .card {
    background: var(--panel); border: 1px solid var(--border); border-radius: 12px;
    padding: 20px; margin-bottom: 20px;
  }
  .card h2 { margin: 0 0 14px 0; font-size: 16px; display: flex; align-items: center; justify-content: space-between; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 10px 8px; border-bottom: 1px solid var(--border); }
  th { color: var(--muted); font-weight: 500; font-size: 12px; }
  input[type=""text""], input[type=""password""], input[type=""number""], select {
    background: var(--bg); color: var(--text); border: 1px solid var(--border);
    padding: 8px 10px; border-radius: 6px; font-size: 13px; width: 100%;
  }
  .form-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 14px; margin-bottom: 14px; }
  .checkbox-row { display: flex; align-items: center; gap: 8px; font-size: 13px; margin-bottom: 10px; cursor: pointer; }
  .mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; }
  .toast {
    position: fixed; bottom: 20px; right: 20px; background: #1e293b; border: 1px solid var(--accent);
    padding: 12px 18px; border-radius: 8px; font-size: 13px; display: none; box-shadow: 0 8px 24px rgba(0,0,0,0.5); z-index: 100;
  }
  .error-box {
    background: rgba(239, 68, 68, 0.12); border: 1px solid rgba(239, 68, 68, 0.4);
    color: #fca5a5; padding: 12px 16px; border-radius: 8px; margin-bottom: 20px; display: none; font-size: 13px;
  }
  .login-overlay {
    position: fixed; inset: 0; background: rgba(10, 14, 22, 0.88);
    display: none; align-items: center; justify-content: center; z-index: 50;
  }
  .login-card {
    background: var(--panel); border: 1px solid var(--border); border-radius: 14px;
    padding: 28px; width: 100%; max-width: 380px; box-shadow: 0 16px 40px rgba(0,0,0,0.6);
  }
  .login-card h2 { margin: 0 0 6px 0; font-size: 18px; }
  .login-card p { margin: 0 0 18px 0; font-size: 13px; color: var(--muted); }
  .login-field { margin-bottom: 14px; }
  .login-field label { display: block; font-size: 12px; color: var(--muted); margin-bottom: 5px; }
</style>
</head>
<body>

<div id=""loginOverlay"" class=""login-overlay"">
  <div class=""login-card"">
    <h2>🔒 Shoko Authentication</h2>
    <p>Sign in with your Shoko Server account.</p>
    <div id=""loginError"" class=""error-box"" style=""margin-bottom:14px;""></div>
    <form onsubmit=""handleLogin(event)"">
      <div class=""login-field"">
        <label>Username</label>
        <input type=""text"" id=""loginUser"" value=""Default"" required autofocus>
      </div>
      <div class=""login-field"">
        <label>Password</label>
        <input type=""password"" id=""loginPass"" placeholder=""Leave empty if no password set"">
      </div>
      <button type=""submit"" class=""primary"" style=""width:100%; justify-content:center; padding:10px;"">Sign In</button>
    </form>
  </div>
</div>

<div class=""container"" id=""appRoot""></div>
<div id=""toast"" class=""toast""></div>

<script>
const TOKEN_KEY = 'shoko_cloud_bridge_apikey';

window.cbSignOut = function() {
  localStorage.removeItem(TOKEN_KEY);
  if (window.cbTimer) clearInterval(window.cbTimer);
  document.getElementById('appRoot').innerHTML = '';
  const dynScript = document.getElementById('cbAppScript');
  if (dynScript) dynScript.remove();
  document.getElementById('loginOverlay').style.display = 'flex';
};

window.cbApiFetch = async function(url, options = {}) {
  const key = localStorage.getItem(TOKEN_KEY) || '';
  const headers = Object.assign({}, options.headers || {});
  if (key) headers['apikey'] = key;
  const res = await fetch(url, Object.assign({}, options, { headers }));
  if (res.status === 401 || res.status === 403) {
    window.cbSignOut();
    throw new Error('Unauthorized');
  }
  return res;
};

async function bootApp() {
  const key = localStorage.getItem(TOKEN_KEY);
  if (!key) {
    document.getElementById('loginOverlay').style.display = 'flex';
    return;
  }
  try {
    const res = await window.cbApiFetch('/api/CloudBridge/app');
    const bundle = await res.json();
    document.getElementById('loginOverlay').style.display = 'none';
    document.getElementById('appRoot').innerHTML = bundle.html || bundle.Html;
    const oldScript = document.getElementById('cbAppScript');
    if (oldScript) oldScript.remove();
    const s = document.createElement('script');
    s.id = 'cbAppScript';
    s.textContent = bundle.js || bundle.Js;
    document.body.appendChild(s);
  } catch (e) {
    if (e.message !== 'Unauthorized') console.error(e);
  }
}

async function handleLogin(e) {
  e.preventDefault();
  const errEl = document.getElementById('loginError');
  errEl.style.display = 'none';
  const user = document.getElementById('loginUser').value.trim();
  const pass = document.getElementById('loginPass').value;
  try {
    const res = await fetch('/api/auth', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ user, pass, device: 'shoko-cloud-bridge' })
    });
    if (!res.ok) {
      errEl.textContent = 'Invalid username or password.';
      errEl.style.display = 'block';
      return;
    }
    const data = await res.json();
    const token = data.apikey || data.ApiKey;
    if (!token) {
      errEl.textContent = 'Could not obtain API key.';
      errEl.style.display = 'block';
      return;
    }
    localStorage.setItem(TOKEN_KEY, token);
    await bootApp();
  } catch (err) {
    errEl.textContent = 'Login failed: ' + err.message;
    errEl.style.display = 'block';
  }
}

bootApp();
</script>
</body>
</html>";
}
