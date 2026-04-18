using Murder;
using Murder.Data;

namespace NeverwayMod.DevTools.Core;

/// <summary>
/// Runtime-discovered catalog of buffs.
/// 
/// Buffs are assets in Neverway.dll (Road.Assets.BuffAsset). We enumerate them from the
/// game's asset database so the list stays in sync with the current game build.
/// </summary>
internal static class BuffCatalog
{
    private static (Guid guid, string name)[]? _cache;
    private static float _cacheTime;

    public static (Guid guid, string name)[] GetAllBuffsCached(float refreshSeconds = 10f)
    {
        float now = Game.Now;
        if (_cache is not null && now - _cacheTime < refreshSeconds)
            return _cache;

        _cache = GetAllBuffsUncached();
        _cacheTime = now;
        return _cache;
    }

    private static (Guid guid, string name)[] GetAllBuffsUncached()
    {
        try
        {
            var buffAssetType = ResolveBuffAssetType();
            if (buffAssetType is null)
                return [];

            var all = Game.Data.FilterAllAssets(buffAssetType);
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

    private static Type? ResolveBuffAssetType()
    {
        const string fqName = "Road.Assets.BuffAsset";
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
