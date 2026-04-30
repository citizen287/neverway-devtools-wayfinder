using System.Reflection;
using Road.Services;

namespace DevTools.Core;

/// <summary>
/// Reflection-based access to Neverway's RoadSaveData downloaded software state.
/// 
/// Computer apps/software are unlocked by having their <see cref="Guid"/> present in
/// RoadSaveData.DownloadedSoftwareAtHome, which is mutated via
/// RoadSaveData.DownloadSoftware(Guid) and RoadSaveData.RemoveSoftware(Guid).
/// </summary>
internal static class ComputerSoftwareSaveController
{
    private static bool _loaded;

    private static Type? _saveType;
    private static PropertyInfo? _downloadedSoftwareAtHome;
    private static MethodInfo? _downloadSoftware;
    private static MethodInfo? _removeSoftware;

    private static string _lastStatus = string.Empty;
    public static string LastStatus => _lastStatus;

    public static object? GetSaveOrNull()
    {
        try { return SaveServices.GetOrCreateSave(); }
        catch { return null; }
    }

    public static bool EnsureLoaded()
    {
        if (_loaded)
            return _saveType is not null;

        _loaded = true;

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { _saveType = asm.GetType("Road.Assets.RoadSaveData", throwOnError: false, ignoreCase: false); }
                catch { continue; }

                if (_saveType is not null)
                    break;
            }

            if (_saveType is null)
            {
                _lastStatus = "RoadSaveData type not found.";
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _downloadedSoftwareAtHome = _saveType.GetProperty("DownloadedSoftwareAtHome", flags);
            _downloadSoftware = _saveType.GetMethod("DownloadSoftware", flags, [typeof(Guid)]);
            _removeSoftware = _saveType.GetMethod("RemoveSoftware", flags, [typeof(Guid)]);

            if (_downloadedSoftwareAtHome is null)
                _lastStatus = "DownloadedSoftwareAtHome property not found.";
            else if (_downloadSoftware is null || _removeSoftware is null)
                _lastStatus = "DownloadSoftware/RemoveSoftware method(s) not found.";
            else
                _lastStatus = string.Empty;

            return _downloadedSoftwareAtHome is not null && _downloadSoftware is not null && _removeSoftware is not null;
        }
        catch (Exception ex)
        {
            _lastStatus = $"ComputerSoftwareSaveController init failed: {ex.Message}";
            return false;
        }
    }

    public static bool IsSoftwareUnlocked(object save, Guid softwareGuid)
    {
        if (!EnsureLoaded())
            return false;

        try
        {
            // DownloadedSoftwareAtHome is ImmutableArray<Guid>.
            if (_downloadedSoftwareAtHome?.GetValue(save) is System.Collections.IEnumerable seq)
            {
                foreach (var v in seq)
                {
                    if (v is Guid g && g == softwareGuid)
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static bool SetSoftwareUnlocked(object save, Guid softwareGuid, bool unlocked)
    {
        if (!EnsureLoaded())
            return false;

        try
        {
            if (_downloadSoftware is null || _removeSoftware is null)
            {
                _lastStatus = "Backend not found.";
                return false;
            }

            if (unlocked)
                _downloadSoftware.Invoke(save, [softwareGuid]);
            else
                _removeSoftware.Invoke(save, [softwareGuid]);

            _lastStatus = $"{(unlocked ? "Unlocked" : "Locked")}: {softwareGuid.ToString()[..8]}";
            return true;
        }
        catch (TargetInvocationException tie)
        {
            _lastStatus = tie.InnerException?.Message ?? tie.Message;
            return false;
        }
        catch (Exception ex)
        {
            _lastStatus = ex.Message;
            return false;
        }
    }
}
