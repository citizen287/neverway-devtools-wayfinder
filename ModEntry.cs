using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using HarmonyLib;
using Wayfinder.API;
using Wayfinder.Core;
/// <summary>
/// Entry point for the new mod loader.
/// The loader looks for a public class named exactly <c>ModEntry</c> in the root namespace,
/// with a public static <c>Start()</c> method.
/// </summary>
public class ModEntry : IWayfinderMod
{
    public string Name => "DevTools";
    public string Description => "Debug/Cheat menu with a lot of features";
    public string Version => "0.3";
    public string Author => "Citizen287";

    // Keep this ID stable across loads so UnpatchAll works reliably.
    private const string HarmonyId = "com.citizen287.Devtools";
    private static Harmony? _harmony;
    private static bool _resolverInstalled;
    private static readonly HashSet<string> _resolverDebugOnce = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loggedCimguiLoad;
    // ImGui.NET is ILRepacked into this mod DLL, so there is no external
    // ImGui.NET.dll to probe for anymore.

    // IMPORTANT:
    // Wayfinder loads mods like this:
    //   1) AssemblyLoadContext.Default.LoadFromAssemblyPath(mod.dll)
    //   2) modAssembly.GetTypes()  (this is where missing deps crash the load)
    //
    // .NET's Default ALC will NOT automatically probe the mod's folder for
    // referenced assemblies (ImGui.NET.dll, 0Harmony.dll, etc). So we must install
    // our resolver at *assembly load time*.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        InstallDependencyResolver();
    }
#pragma warning restore CA2255

    void IWayfinderMod.Start() => Start();
    void IWayfinderMod.Stop() => Stop();

    public static void Start()
    {
        // Wayfinder calls modAssembly.GetTypes() before instantiating mods, so we
        // cannot rely on this resolver for *managed* dependencies required for type
        // loading (e.g. ImGui.NET.dll). Those must be shipped next to the mod DLL,
        // AND we must install the resolver via ModuleInitializer so Default ALC can
        // actually find them.
        //
        // We still keep this resolver for native libs (cimgui) and for any loader
        // edge-cases where dependency search paths differ.
        InstallDependencyResolver();

        try
        {
            _harmony = new Harmony(HarmonyId);

            // If this mod uses Harmony attributes, apply them.
            _harmony.PatchAll();

            // This repo also uses explicit patch application.
            DevTools.GameDrawPatch.Apply(_harmony);
        }
        catch (Exception ex)
        {
            try
            {
                LoaderCore.LogError("Failed to inject: " + ex);
            }
            catch
            {
                // If LoaderCore logging fails for any reason, fall back to the mod's logger.
                DevTools.DevToolsMod.LogError("Failed to inject: " + ex);
            }
        }
    }

    /// <summary>
    /// Optional stop hook (only called if the loader supports unloading).
    /// </summary>
    public static void Stop()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
    }

    /// <summary>
    /// Ensures external dependency DLLs (e.g. ImGui.NET.dll) can be loaded when the loader
    /// only loads the main mod assembly.
    ///
    /// We resolve assemblies from the same directory as this mod DLL.
    /// </summary>
    private static void InstallDependencyResolver()
    {
        try
        {
            if (_resolverInstalled)
                return;
            _resolverInstalled = true;

            // Some mod loaders load assemblies from a stream, making Assembly.Location empty.
            // In that case, fall back to the game's base directory.
            string? modPath = typeof(ModEntry).Assembly.Location;

            string? modDir = null;
            if (!string.IsNullOrWhiteSpace(modPath))
                modDir = Path.GetDirectoryName(modPath);

            if (string.IsNullOrWhiteSpace(modDir) || !Directory.Exists(modDir))
                modDir = AppContext.BaseDirectory;

            // Prefer the actual process (game) directory when available.
            // This is more reliable than Process.MainModule on some platforms.
            string? processDir = null;
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                    processDir = Path.GetDirectoryName(processPath);
            }
            catch { /* ignore */ }

            // Also probe the Wayfinder.Core location if present.
            string? wayfinderDir = null;
            try
            {
                var wfPath = typeof(IWayfinderMod).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(wfPath))
                    wayfinderDir = Path.GetDirectoryName(wfPath);
            }
            catch { /* ignore */ }

            if (string.IsNullOrWhiteSpace(modDir) || !Directory.Exists(modDir))
            {
                Console.WriteLine("[Neverway DevTools] Warning: Could not determine a valid probe directory for dependency resolution.");
                return;
            }


            var modAlc = AssemblyLoadContext.GetLoadContext(typeof(ModEntry).Assembly) ?? AssemblyLoadContext.Default;

            var probeDirs = BuildProbeDirs(modDir, processDir, wayfinderDir);

            Assembly? Resolver(AssemblyLoadContext alc, AssemblyName name)
            {
                // Ignore resource satellite assemblies.
                if (name.Name is null || name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                    return null;

                // If it's already loaded anywhere, reuse it.
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var an = a.GetName();
                        if (string.Equals(an.Name, name.Name, StringComparison.OrdinalIgnoreCase))
                            return a;
                    }
                    catch { /* ignore */ }
                }

                foreach (var dir in probeDirs)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;

                    string candidatePath = Path.Combine(dir, $"{name.Name}.dll");
                    if (!File.Exists(candidatePath))
                    {
                        // Linux is typically case-sensitive and some distributions/modpacks ship
                        // different casing (e.g. imgui.net.dll). Try a case-insensitive match.
                        candidatePath = TryFindFileCaseInsensitive(dir, $"{name.Name}.dll") ?? candidatePath;
                        if (!File.Exists(candidatePath))
                            continue;
                    }

                    try
                    {
                        // IMPORTANT: load into the requesting ALC.
                        return alc.LoadFromAssemblyPath(candidatePath);
                    }
                    catch
                    {
                        // keep probing
                    }
                }

                // Fallback: some distributions place managed deps in subfolders.
                // For critical deps (Harmony), do a bounded recursive search.
                if (name.Name is not null &&
                    string.Equals(name.Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                {
                    // Also probe the NuGet global packages folder in case the loader doesn't
                    // include it in its default resolution paths.
                    try
                    {
                        var nugetGlobal = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        var nugetPackages = Path.Combine(nugetGlobal, ".nuget", "packages");
                        if (Directory.Exists(nugetPackages))
                        {
                            var nugetHit = TryFindFileRecursiveCaseInsensitive(nugetPackages, $"{name.Name}.dll", maxDepth: 6, maxCandidates: 50);
                            if (nugetHit is not null)
                            {
                                try { return alc.LoadFromAssemblyPath(nugetHit); }
                                catch { /* ignore and keep searching */ }
                            }
                        }
                    }
                    catch { /* ignore */ }

                    foreach (var dir in probeDirs)
                    {
                        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                            continue;

                        var foundPath = TryFindFileRecursiveCaseInsensitive(dir, $"{name.Name}.dll", maxDepth: 8, maxCandidates: 5000);
                        if (foundPath is null)
                            continue;

                        try { return alc.LoadFromAssemblyPath(foundPath); }
                        catch { /* ignore and keep searching */ }
                    }
                }

                // Debug output (once per assembly) to help diagnose loader-specific search paths.
                if (name.Name is not null && _resolverDebugOnce.Add(name.Name))
                {
                    Console.WriteLine($"[Neverway DevTools] Failed to resolve '{name.FullName}'. Probed: {string.Join("; ", probeDirs)}");
                }

                return null;
            }

            // Hook both the mod's ALC and Default as a belt-and-suspenders approach.
            modAlc.Resolving += Resolver;
            if (!ReferenceEquals(modAlc, AssemblyLoadContext.Default))
                AssemblyLoadContext.Default.Resolving += Resolver;

            // Ensure the ImGui.NET native library (cimgui) is resolved from our probe dirs.
            // Without this, the process may pick up an incompatible cimgui from elsewhere.
            //
            // ImGui.NET is ILRepacked into this mod DLL, so the DllImport attributes live
            // in *this* assembly. Install the resolver here.
            InstallImGuiNativeResolver(typeof(ModEntry).Assembly, probeDirs);

            // Some loaders still rely on AppDomain resolution in certain cases.
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    var asmName = new AssemblyName(args.Name);
                    return Resolver(modAlc, asmName);
                }
                catch
                {
                    return null;
                }
            };

            Console.WriteLine($"[Neverway DevTools] Dependency resolver installed (primary dir: {modDir}, alc: {modAlc.Name ?? "<default>"})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Neverway DevTools] Warning: Failed to install dependency resolver: {ex}");
        }
    }

    private static string[] BuildProbeDirs(string modDir, string? processDir, string? wayfinderDir)
    {
        var dirs = new List<string>(capacity: 8);

        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d))
                return;
            try
            {
                d = Path.GetFullPath(d);
            }
            catch
            {
                // ignore invalid paths
            }

            if (!Directory.Exists(d))
                return;

            if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(d);
        }

        Add(modDir);
        Add(AppContext.BaseDirectory);
        Add(Environment.CurrentDirectory);
        Add(processDir);
        Add(wayfinderDir);

        // Common layout: <GameDir>/Mods/*.dll
        try { Add(Path.Combine(AppContext.BaseDirectory, "Mods")); } catch { }

        // Common layout: <GameDir>/Wayfinder/*.dll
        try { Add(Path.Combine(AppContext.BaseDirectory, "Wayfinder")); } catch { }

        // Directory containing the game executable (when available).
        try
        {
            var mainModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
            Add(Path.GetDirectoryName(mainModule?.FileName));
        }
        catch
        {
            // may be blocked in some sandboxed environments
        }

        // If mods live under /GameDir/Mods/<mod>/, probe /GameDir and /GameDir/Mods too.
        // If base dir is /GameDir/.modded/, probe /GameDir.
        foreach (var seed in new[] { modDir, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(seed))
                continue;

            try
            {
                var d = new DirectoryInfo(seed);
                for (int i = 0; i < 4 && d.Parent != null; i++)
                {
                    // Look for known folder names and probe their parent as "game root".
                    string name = d.Name;
                    if (name.Equals("Mods", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".modded", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(d.Parent.FullName);
                        Add(Path.Combine(d.Parent.FullName, "Mods"));
                        Add(Path.Combine(d.Parent.FullName, ".modded"));
                        break;
                    }

                    d = d.Parent;
                }
            }
            catch
            {
                // ignore
            }
        }

        return dirs.ToArray();
    }

    private static string? TryFindFileCaseInsensitive(string dir, string fileName)
    {
        try
        {
            if (!Directory.Exists(dir))
                return null;

            // Avoid enumerating huge dirs if we can.
            // We'll just scan direct children and compare case-insensitively.
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? TryFindFileRecursiveCaseInsensitive(string dir, string fileName, int maxDepth, int maxCandidates)
    {
        try
        {
            if (maxDepth < 0 || maxCandidates <= 0)
                return null;
            if (!Directory.Exists(dir))
                return null;

            var queue = new Queue<(string path, int depth)>();
            queue.Enqueue((dir, 0));

            int seen = 0;
            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                if (depth > maxDepth)
                    continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(current); }
                catch { continue; }

                foreach (var f in files)
                {
                    if (string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                        return f;

                    if (++seen >= maxCandidates)
                        break;
                }

                if (seen >= maxCandidates)
                    break;

                if (depth == maxDepth)
                    continue;

                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(current); }
                catch { continue; }

                foreach (var sd in subDirs)
                    queue.Enqueue((sd, depth + 1));
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void InstallImGuiNativeResolver(Assembly? imguiAssembly, string[] probeDirs)
    {
        try
        {
            if (imguiAssembly is null)
                return;

            NativeLibrary.SetDllImportResolver(imguiAssembly, (libraryName, assembly, searchPath) =>
            {
                // ImGui.NET uses DllImport("cimgui").
                if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                bool sawIncompatibleCandidate = false;

                // Candidate filenames across platforms.
                // NOTE: We also probe common ImGui.NET nuget/native folder layouts to make it
                // easier to drop the runtimes folder next to the game or mod.
                string[] candidates =
                [
                    // Prefer uniquely named binaries shipped with this mod to avoid collisions with
                    // other loaders/mods that also ship a (possibly incompatible) libcimgui.
                    "cimgui.neverway-devtools.dll",
                    "libcimgui.neverway-devtools.so",
                    "libcimgui.neverway-devtools.dylib",

                    // Standard names
                    "cimgui.dll",        // Windows
                    "libcimgui.so",      // Linux
                    "cimgui.so",         // Linux (some builds ship without the lib prefix)
                    "libcimgui.dylib",   // macOS

                    // Some loaders/packaging use these layouts
                    Path.Combine("runtimes", "linux-x64", "native", "libcimgui.so"),
                    Path.Combine("runtimes", "win-x64", "native", "cimgui.dll"),
                    Path.Combine("runtimes", "win-arm64", "native", "cimgui.dll"),
                    Path.Combine("runtimes", "osx", "native", "libcimgui.dylib"),

                    "cimgui"             // last resort
                ];

                foreach (var dir in probeDirs)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;

                    foreach (var file in candidates)
                    {
                        string fullPath = Path.Combine(dir, file);
                        if (!File.Exists(fullPath))
                            continue;

                        try
                        {
                            var handle = NativeLibrary.Load(fullPath);


                            try
                            {
                                _ = NativeLibrary.GetExport(handle, "igGetIO");
                                if (!_loggedCimguiLoad)
                                {
                                    _loggedCimguiLoad = true;
                                    Console.WriteLine($"[Neverway DevTools] Loaded native cimgui from: {fullPath}");
                                }
                                return handle;
                            }
                            catch
                            {
                                Console.WriteLine($"[Neverway DevTools] Found native cimgui at '{fullPath}' but it did not export 'igGetIO' (likely incompatible build). Trying next candidate...");
                                sawIncompatibleCandidate = true;
                                try { NativeLibrary.Free(handle); } catch { }
                                continue;
                            }
                        }
                        catch
                        {

                        }
                    }
                }

                if (sawIncompatibleCandidate)
                {
                    throw new DllNotFoundException(
                        $"Found one or more '{libraryName}' native libraries, but none were compatible (missing export 'igGetIO'). " +
                        $"Install the ImGui.NET-provided libcimgui for your platform next to the mod. Probed: {string.Join("; ", probeDirs)}");
                }

                Console.WriteLine($"[Neverway DevTools] Failed to resolve native library '{libraryName}'. Probed: {string.Join("; ", probeDirs)}");
                return IntPtr.Zero;
            });

            // Pre-load once so we fail early (and log paths) instead of failing in the draw hook.
            _ = NativeLibrary.TryLoad("cimgui", imguiAssembly, DllImportSearchPath.AssemblyDirectory, out _);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Neverway DevTools] Warning: Failed to install ImGui native resolver: {ex}");
        }
    }
}