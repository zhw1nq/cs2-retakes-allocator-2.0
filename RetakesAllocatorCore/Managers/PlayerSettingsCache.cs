using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;

namespace RetakesAllocatorCore.Managers;

/// <summary>
/// In-memory cache for player weapon preferences.
/// Eliminates database blocking on main thread by caching settings in memory
/// and performing batch writes periodically.
/// </summary>
public static class PlayerSettingsCache
{
    private static readonly ConcurrentDictionary<ulong, UserSetting> _cache = new();
    private static readonly ConcurrentBag<ulong> _dirtyPlayers = new();
    private static readonly SemaphoreSlim _flushSemaphore = new(1, 1);

    /// <summary>
    /// Gets cached settings for a player. Returns null if not in cache.
    /// This is instant (no DB call) and safe to call from main thread.
    /// </summary>
    public static UserSetting? GetSettings(ulong userId)
    {
        if (userId == 0) return null;
        return _cache.TryGetValue(userId, out var settings) ? settings : null;
    }

    /// <summary>
    /// Gets or creates settings for a player from cache.
    /// </summary>
    public static UserSetting GetOrCreateSettings(ulong userId)
    {
        if (userId == 0)
        {
            return new UserSetting { UserId = 0 };
        }

        return _cache.GetOrAdd(userId, id => new UserSetting { UserId = id });
    }

    /// <summary>
    /// Sets weapon preference in cache and marks player as dirty for next flush.
    /// This is instant (no DB call) and safe to call from main thread.
    /// </summary>
    public static void SetWeaponPreference(ulong userId, CsTeam team, WeaponAllocationType type, CsItem? weapon)
    {
        if (userId == 0) return;

        var settings = GetOrCreateSettings(userId);
        settings.SetWeaponPreference(team, type, weapon);
        MarkDirty(userId);
    }

    /// <summary>
    /// Sets AWP/sniper preference in cache.
    /// </summary>
    public static void SetPreferredWeapon(ulong userId, CsItem? weapon)
    {
        if (userId == 0) return;

        var settings = GetOrCreateSettings(userId);
        settings.SetWeaponPreference(CsTeam.Terrorist, WeaponAllocationType.Preferred,
            WeaponHelpers.CoercePreferredTeam(weapon, CsTeam.Terrorist));
        settings.SetWeaponPreference(CsTeam.CounterTerrorist, WeaponAllocationType.Preferred,
            WeaponHelpers.CoercePreferredTeam(weapon, CsTeam.CounterTerrorist));
        MarkDirty(userId);
    }

    /// <summary>
    /// Sets Zeus preference in cache.
    /// </summary>
    public static void SetZeusPreference(ulong userId, bool enabled)
    {
        if (userId == 0) return;

        var settings = GetOrCreateSettings(userId);
        settings.ZeusEnabled = enabled;
        MarkDirty(userId);
    }

    /// <summary>
    /// Sets enemy stuff preference in cache.
    /// </summary>
    public static void SetEnemyStuffPreference(ulong userId, EnemyStuffTeamPreference preference)
    {
        if (userId == 0) return;

        var settings = GetOrCreateSettings(userId);
        settings.EnemyStuffTeamPreference = preference;
        MarkDirty(userId);
    }

    /// <summary>
    /// Pre-loads player settings from database into cache.
    /// Call this async when player connects.
    /// </summary>
    public static async Task LoadPlayerAsync(ulong userId)
    {
        if (userId == 0) return;

        try
        {
            var dbSettings = await Queries.GetUserSettings(userId);
            if (dbSettings != null)
            {
                _cache[userId] = dbSettings;
                Log.Debug($"[Cache] Loaded settings for player {userId}");
            }
            else
            {
                // Create empty settings if player is new
                _cache.TryAdd(userId, new UserSetting { UserId = userId });
                Log.Debug($"[Cache] Created new settings for player {userId}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Cache] Failed to load settings for {userId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Pre-loads multiple players from database. Used during round allocation.
    /// </summary>
    public static async Task LoadPlayersAsync(ICollection<ulong> userIds)
    {
        if (userIds.Count == 0) return;

        // Filter out already cached players
        var toLoad = userIds.Where(id => id != 0 && !_cache.ContainsKey(id)).ToList();
        if (toLoad.Count == 0) return;

        try
        {
            var dbSettings = await Queries.GetUsersSettingsAsync(toLoad);
            foreach (var kvp in dbSettings)
            {
                _cache[kvp.Key] = kvp.Value;
            }

            // Add empty settings for players not in DB
            foreach (var userId in toLoad.Where(id => !dbSettings.ContainsKey(id)))
            {
                _cache.TryAdd(userId, new UserSetting { UserId = userId });
            }

            Log.Debug($"[Cache] Bulk loaded {dbSettings.Count} players, created {toLoad.Count - dbSettings.Count} new");
        }
        catch (Exception ex)
        {
            Log.Error($"[Cache] Failed to bulk load players: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all cached settings for specified players.
    /// Returns immediately from cache - no DB call.
    /// </summary>
    public static IDictionary<ulong, UserSetting> GetCachedSettings(ICollection<ulong> userIds)
    {
        var result = new Dictionary<ulong, UserSetting>();
        foreach (var userId in userIds)
        {
            if (userId != 0 && _cache.TryGetValue(userId, out var settings))
            {
                result[userId] = settings;
            }
        }
        return result;
    }

    /// <summary>
    /// Marks a player as dirty (needs DB write).
    /// </summary>
    private static void MarkDirty(ulong userId)
    {
        _dirtyPlayers.Add(userId);
    }

    /// <summary>
    /// Flushes dirty player data to database.
    /// Call this periodically (e.g., every 120 seconds) and on player disconnect.
    /// </summary>
    public static async Task FlushDirtyPlayersAsync()
    {
        if (_dirtyPlayers.IsEmpty) return;

        // Collect all dirty players
        var toFlush = new HashSet<ulong>();
        while (_dirtyPlayers.TryTake(out var userId))
        {
            toFlush.Add(userId);
        }

        if (toFlush.Count == 0) return;

        // Prevent concurrent flushes
        if (!await _flushSemaphore.WaitAsync(0))
        {
            // Another flush is in progress, re-add players
            foreach (var userId in toFlush)
            {
                _dirtyPlayers.Add(userId);
            }
            return;
        }

        try
        {
            Log.Debug($"[Cache] Flushing {toFlush.Count} dirty players to database");
            var flushed = 0;

            foreach (var userId in toFlush)
            {
                if (_cache.TryGetValue(userId, out var settings))
                {
                    try
                    {
                        await Queries.UpsertUserSettingsFromCacheAsync(userId, settings);
                        flushed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Cache] Failed to flush player {userId}: {ex.Message}");
                        // Re-add to dirty list for retry
                        _dirtyPlayers.Add(userId);
                    }
                }
            }

            Log.Debug($"[Cache] Successfully flushed {flushed}/{toFlush.Count} players");
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    /// <summary>
    /// Flushes a single player to database and removes from cache.
    /// Call this on player disconnect.
    /// </summary>
    public static async Task FlushPlayerAsync(ulong userId)
    {
        if (userId == 0) return;

        if (_cache.TryRemove(userId, out var settings))
        {
            try
            {
                await Queries.UpsertUserSettingsFromCacheAsync(userId, settings);
                Log.Debug($"[Cache] Flushed and removed player {userId} from cache");
            }
            catch (Exception ex)
            {
                Log.Error($"[Cache] Failed to flush player {userId} on disconnect: {ex.Message}");
                // Re-add to cache for next flush attempt
                _cache.TryAdd(userId, settings);
                _dirtyPlayers.Add(userId);
            }
        }
    }

    /// <summary>
    /// Clears all cached data. Call this on map change or plugin unload.
    /// </summary>
    public static async Task ClearAsync()
    {
        // Flush before clearing
        await FlushDirtyPlayersAsync();
        _cache.Clear();
        Log.Debug("[Cache] Cache cleared");
    }

    /// <summary>
    /// Gets cache statistics for debugging.
    /// </summary>
    public static (int CachedPlayers, int DirtyPlayers) GetStats()
    {
        return (_cache.Count, _dirtyPlayers.Count);
    }
}
