namespace DevTools;

/// <summary>
/// Legacy entrypoint for the old MurderModLoader.
///
/// The new loader uses <see cref="ModEntry"/> instead.
/// We keep this class around as a convenient place for shared static state used
/// throughout the mod (e.g., logging + overlay toggles).
/// </summary>
public static class DevToolsMod
{
    internal static bool ShowOverlay = true;

    internal static void LogInfo(string message) => Console.WriteLine($"[Neverway DevTools] {message}");
    internal static void LogWarning(string message) => Console.WriteLine($"[Neverway DevTools][Warn] {message}");
    internal static void LogError(string message) => Console.WriteLine($"[Neverway DevTools][Error] {message}");
}
