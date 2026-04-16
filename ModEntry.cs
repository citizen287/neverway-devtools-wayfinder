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
            var probeDirs = new[]
            {
                modDir,
                AppContext.BaseDirectory,
                Environment.CurrentDirectory
            };

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
                        continue;

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
            foreach (var dll in new[] { "ImGui.NET.dll" })
            {
                foreach (var dir in probeDirs)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;

                    string dep = Path.Combine(dir, dll);
                    if (!File.Exists(dep))
                        continue;

                    try { modAlc.LoadFromAssemblyPath(dep); }
                    catch { /* ignore */ }
                    break;
                }
            }

            // Ensure the ImGui.NET native library (cimgui) is resolved from our probe dirs.
            // Without this, the process may pick up an incompatible cimgui from elsewhere.
            InstallImGuiNativeResolver(probeDirs);

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

    private static void InstallImGuiNativeResolver(string[] probeDirs)
    {
        try
        {
            Assembly imguiAssembly;
            try
            {
                imguiAssembly = Assembly.Load("ImGui.NET");
            }
            catch
            {
                // If ImGui.NET isn't present, we can't install a resolver.
                return;
            }

            NativeLibrary.SetDllImportResolver(imguiAssembly, (libraryName, assembly, searchPath) =>
            {
                // ImGui.NET uses DllImport("cimgui").
                if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                // Candidate filenames across platforms.
                string[] candidates =
                [
                    // Prefer uniquely named binaries shipped with this mod to avoid collisions with
                    // other loaders/mods that also ship a (possibly incompatible) libcimgui.
                    "cimgui.neverway-devtools.dll",
                    "libcimgui.neverway-devtools.so",
                    "libcimgui.neverway-devtools.dylib",

                    "cimgui.dll",        // Windows
                    "libcimgui.so",      // Linux
                    "libcimgui.dylib",   // macOS
                    "cimgui"             // some loaders
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