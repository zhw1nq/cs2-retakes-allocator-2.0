using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using RetakesAllocatorCore.Db;

namespace RetakesAllocatorCore.Managers;

/// <summary>
/// In-memory cache for player weapon preferences with write-behind persistence.
/// Eliminates synchronous database queries at round start and during commands.
/// </summary>
public class PlayerPreferenceCache
{
    private static PlayerPreferenceCache? _instance;
    public static PlayerPreferenceCache Instance => _instance ??= new PlayerPreferenceCache();

    private readonly ConcurrentDictionary<ulong, UserSetting> _cache = new();
    private readonly ConcurrentDictionary<ulong, bool> _dirtyFlags = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private Timer? _flushTimer;
    private bool _isInitialized;

    /// <summary>
    /// Interval in seconds between automatic flush of dirty entries.
    /// </summary>
    private const int FlushIntervalSeconds = 30;

    /// <summary>
    /// Initialize the cache and start the background flush timer.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;

        _flushTimer = new Timer(
            _ => Task.Run(FlushDirtyEntriesAsync),
            null,
            TimeSpan.FromSeconds(FlushIntervalSeconds),
            TimeSpan.FromSeconds(FlushIntervalSeconds)
        );
        _isInitialized = true;
        Log.Debug("PlayerPreferenceCache initialized with write-behind strategy");
    }

    /// <summary>
    /// Get cached user settings. Returns null if not in cache.
    /// </summary>
    public UserSetting? Get(ulong steamId)
    {
        _cache.TryGetValue(steamId, out var setting);
        return setting;
    }

    /// <summary>
    /// Get cached settings for multiple users.
    /// </summary>
    public IDictionary<ulong, UserSetting> GetMultiple(ICollection<ulong> steamIds)
    {
        var result = new Dictionary<ulong, UserSetting>();
        foreach (var id in steamIds)
        {
            if (_cache.TryGetValue(id, out var setting))
            {
                result[id] = setting;
            }
        }
        return result;
    }

    /// <summary>
    /// Set user setting in cache and mark as dirty for later persistence.
    /// </summary>
    public void Set(ulong steamId, UserSetting setting)
    {
        if (steamId == 0) return;

        _cache[steamId] = setting;
        _dirtyFlags[steamId] = true;
    }

    /// <summary>
    /// Update a specific field in user settings without full replacement.
    /// </summary>
    public void Update(ulong steamId, Action<UserSetting> mutation)
    {
        if (steamId == 0) return;

        var setting = _cache.GetOrAdd(steamId, _ => new UserSetting { UserId = steamId });
        mutation(setting);
        _dirtyFlags[steamId] = true;
    }

    /// <summary>
    /// Mark a player's settings for flush (but don't flush immediately).
    /// </summary>
    public void MarkDirty(ulong steamId)
    {
        if (steamId == 0) return;
        _dirtyFlags[steamId] = true;
    }

    /// <summary>
    /// Pre-load player preferences from database into cache.
    /// Used at round start to avoid synchronous queries.
    /// </summary>
    public async Task PreloadPlayersAsync(ICollection<ulong> playerIds)
    {
        if (playerIds.Count == 0) return;

        var missing = playerIds.Where(id => id != 0 && !_cache.ContainsKey(id)).ToList();
        if (missing.Count == 0) return;

        try
        {
            var settings = await Queries.GetUsersSettingsAsync(missing);
            foreach (var kvp in settings)
            {
                _cache.TryAdd(kvp.Key, kvp.Value);
            }
            Log.Debug($"PlayerPreferenceCache: Pre-loaded {settings.Count} player preferences");
        }
        catch (Exception ex)
        {
            Log.Error($"PlayerPreferenceCache: Failed to preload players: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove a player from cache (on disconnect).
    /// Marks for flush before removal if dirty.
    /// </summary>
    public void Remove(ulong steamId)
    {
        if (steamId == 0) return;

        // If dirty, queue for immediate flush before removal
        if (_dirtyFlags.TryRemove(steamId, out _))
        {
            // Flush this specific player immediately in background
            Task.Run(async () =>
            {
                if (_cache.TryGetValue(steamId, out var setting))
                {
                    await FlushSingleEntryAsync(steamId, setting);
                }
                _cache.TryRemove(steamId, out _);
            });
        }
        else
        {
            _cache.TryRemove(steamId, out _);
        }
    }

    /// <summary>
    /// Flush all dirty entries to database. Called periodically and on map change.
    /// </summary>
    public async Task FlushDirtyEntriesAsync()
    {
        var dirtyIds = _dirtyFlags.Keys.ToList();
        if (dirtyIds.Count == 0) return;

        await _flushLock.WaitAsync();
        try
        {
            var flushed = 0;
            foreach (var id in dirtyIds)
            {
                if (_cache.TryGetValue(id, out var setting) && _dirtyFlags.TryRemove(id, out _))
                {
                    await FlushSingleEntryAsync(id, setting);
                    flushed++;
                }
            }

            if (flushed > 0)
            {
                Log.Debug($"PlayerPreferenceCache: Flushed {flushed} dirty entries to database");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"PlayerPreferenceCache: Flush error: {ex.Message}");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>
    /// Flush a single entry to database.
    /// </summary>
    private async Task FlushSingleEntryAsync(ulong steamId, UserSetting setting)
    {
        try
        {
            await Queries.UpsertUserSettingAsync(steamId, setting);
        }
        catch (Exception ex)
        {
            Log.Error($"PlayerPreferenceCache: Failed to flush {steamId}: {ex.Message}");
            // Re-mark as dirty for retry
            _dirtyFlags[steamId] = true;
        }
    }

    /// <summary>
    /// Synchronously flush all entries. Used at map change/shutdown.
    /// </summary>
    public void FlushAllSync()
    {
        FlushDirtyEntriesAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Clear all cached data (on map change).
    /// Does NOT flush dirty entries - call FlushAllSync first if needed.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _dirtyFlags.Clear();
    }

    /// <summary>
    /// Dispose the cache and flush remaining dirty entries.
    /// </summary>
    public void Dispose()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;

        // Final flush on dispose
        try
        {
            FlushAllSync();
        }
        catch (Exception ex)
        {
            Log.Error($"PlayerPreferenceCache: Dispose flush error: {ex.Message}");
        }

        Clear();
        _isInitialized = false;
        _instance = null;
    }

    /// <summary>
    /// Get count of cached entries (for debugging).
    /// </summary>
    public int CachedCount => _cache.Count;

    /// <summary>
    /// Get count of dirty entries (for debugging).
    /// </summary>
    public int DirtyCount => _dirtyFlags.Count;
}
