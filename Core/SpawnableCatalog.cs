using Murder;
using Murder.Assets;
using Murder.Data;

namespace NeverwayMod.DevTools.Core;

/// <summary>
/// Runtime-discovered catalog of spawnable prefabs.
/// 
/// The Spawn panel historically used an embedded <c>entities.json</c> list, which can go stale.
/// This enumerates all <see cref="PrefabAsset"/> from the game's asset database.
/// </summary>
internal static class SpawnableCatalog
{
    private static (Guid guid, string name)[]? _cache;
    private static float _cacheTime;

    public static (Guid guid, string name)[] GetAllPrefabsCached(float refreshSeconds = 5f)
    {
        float now = Game.Now;
        if (_cache is not null && now - _cacheTime < refreshSeconds)
            return _cache;

        _cache = GetAllPrefabsUncached();
        _cacheTime = now;
        return _cache;
    }

    private static (Guid guid, string name)[] GetAllPrefabsUncached()
    {
        try
        {
            var all = Game.Data.FilterAllAssets(typeof(PrefabAsset));
            var list = new List<(Guid guid, string name)>(all.Count);

            foreach (var kv in all)
            {
                string? name = kv.Value?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = kv.Key.ToString()[..8];

                list.Add((kv.Key, name));
            }

            list.Sort(static (a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return list.ToArray();
        }
        catch
        {
            return [];
        }
    }
}
