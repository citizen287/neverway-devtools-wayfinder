using Murder;
using Murder.Data;

namespace NeverwayMod.DevTools.Core;

/// <summary>
/// Runtime-discovered catalog of item assets.
/// 
/// The old devtools panels used an embedded <c>items.json</c>. That file can get out of sync with
/// the game; instead, we enumerate the game's asset database for <c>Road.Assets.ItemAsset</c>.
/// </summary>
internal static class ItemCatalog
{
    // Cache because scanning the asset database every ImGui frame is wasteful.
    private static (Guid guid, string name)[]? _cache;
    private static float _cacheTime;

    /// <summary>Get all items (guid + display name), cached for a short time.</summary>
    public static (Guid guid, string name)[] GetAllItemsCached(float refreshSeconds = 5f)
    {
        float now = Game.Now;
        if (_cache is not null && now - _cacheTime < refreshSeconds)
            return _cache;

        _cache = GetAllItemsUncached();
        _cacheTime = now;
        return _cache;
    }

    private static (Guid guid, string name)[] GetAllItemsUncached()
    {
        try
        {
            // ItemAsset type is defined in Neverway.dll (Road.Assets.ItemAsset), so we resolve it by
            // reflection to avoid needing a direct compile-time dependency on its namespace.
            var itemAssetType = ResolveItemAssetType();
            if (itemAssetType is null)
                return [];

            var all = Game.Data.FilterAllAssets(itemAssetType);
            var list = new List<(Guid guid, string name)>(all.Count);
            foreach (var kv in all)
            {
                // Use the asset's Name if available. Otherwise fall back to its guid prefix.
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

    private static Type? ResolveItemAssetType()
    {
        // Prefer the fully-qualified known name.
        const string fqName = "Road.Assets.ItemAsset";

        // Search all loaded assemblies (Neverway, mods, etc.).
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fqName, throwOnError: false, ignoreCase: false);
                if (t is not null)
                    return t;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }
}
