using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using HarmonyLib;

/// <summary>
/// Entry point for the new mod loader.
/// The loader looks for a public class named exactly <c>ModEntry</c> in the root namespace,
/// with a public static <c>Start()</c> method.
/// </summary>
public class ModEntry
{
    // Keep this ID stable across loads so UnpatchAll works reliably.
    private const string HarmonyId = "neverway.devtools";
    private static Harmony? _harmony;
    private static bool _resolverInstalled;
    private static readonly HashSet<string> _resolverDebugOnce = new(StringComparer.OrdinalIgnoreCase);

    public static void Start()
    {
        InstallDependencyResolver();

        Console.WriteLine("[Neverway DevTools] Loaded (new loader entrypoint)");

        _harmony = new Harmony(HarmonyId);

        try
        {
            DevTools.GameDrawPatch.Apply(_harmony);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Neverway DevTools] Harmony patch failed: {ex}");
        }
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

            if (string.IsNullOrWhiteSpace(modDir) || !Directory.Exists(modDir))
            {
                Console.WriteLine("[Neverway DevTools] Warning: Could not determine a valid probe directory for dependency resolution.");
                return;
            }

            // Use the AssemblyLoadContext that actually loaded this mod.
            // Many mod loaders use a custom ALC, and resolution events are scoped to that context.
            var modAlc = AssemblyLoadContext.GetLoadContext(typeof(ModEntry).Assembly) ?? AssemblyLoadContext.Default;

            // Directories to probe for sibling DLLs.
            // Primary is the directory containing this mod DLL.
            //
            // IMPORTANT: In Wayfinder/Neverway the mod may be loaded from /GameDir/Mods/<mod>/,
            // while shared/native libs might live in the game root (e.g. /GameDir/).
            // Some loaders also run with AppContext.BaseDirectory pointing at /GameDir/.modded/.
            // So we probe a handful of "likely game root" locations derived from what we can see.
            var probeDirs = BuildProbeDirs(modDir);

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

            // Proactively load common managed dependencies if present.
            // This avoids relying on the resolver event firing in some edge cases.
            Assembly? imguiNetAsm = null;
            foreach (var dll in new[] { "ImGui.NET.dll" })
            {
                foreach (var dir in probeDirs)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;

                    string dep = Path.Combine(dir, dll);
                    if (!File.Exists(dep))
                    {
                        dep = TryFindFileCaseInsensitive(dir, dll) ?? dep;
                        if (!File.Exists(dep))
                            continue;
                    }

                    try { imguiNetAsm = modAlc.LoadFromAssemblyPath(dep); }
                    catch { /* ignore */ }
                    break;
                }
            }

            // Ensure the ImGui.NET native library (cimgui) is resolved from our probe dirs.
            // Without this, the process may pick up an incompatible cimgui from elsewhere.
            InstallImGuiNativeResolver(imguiNetAsm, probeDirs);

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

    private static string[] BuildProbeDirs(string modDir)
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

    private static void InstallImGuiNativeResolver(Assembly? imguiAssembly, string[] probeDirs)
    {
        try
        {
            // IMPORTANT: ImGui.NET must be resolved in the *same* AssemblyLoadContext as the mod.
            // Using Assembly.Load("ImGui.NET") can load it into the Default ALC, which means the
            // DllImportResolver we install won't be used by the actual ImGui.NET instance.
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

                            // Validate that this is a compatible cimgui build by checking for a known export.
                            // If this export is missing, ImGui.NET will crash later with EntryPointNotFound.
                            try
                            {
                                _ = NativeLibrary.GetExport(handle, "igGetIO");
                                Console.WriteLine($"[Neverway DevTools] Loaded native cimgui from: {fullPath}");
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
                            // try next
                        }
                    }
                }

                // If we saw at least one cimgui binary but it was incompatible, do NOT fall back to
                // the runtime's default native library resolution (which might load that incompatible
                // binary anyway and then crash later with EntryPointNotFound).
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