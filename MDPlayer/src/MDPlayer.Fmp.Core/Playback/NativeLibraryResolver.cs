using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Fmp.Core.Playback;

/// <summary>
/// Single per-assembly DllImportResolver for Fmp.Core. Both native session
/// backends (OPNA, SPC) resolve their shared libraries through this helper so
/// the two static constructors never collide:
/// <see cref="NativeLibrary.SetDllImportResolver"/> permits only one resolver
/// per assembly, so a second registration throws InvalidOperationException
/// once both session classes initialize in the same process (which happens
/// whenever the full test suite runs). Each backend registers its library
/// name; the resolver dispatches to the backend's cached handle on demand.
/// </summary>
internal static class NativeLibraryResolver
{
    private static readonly ConcurrentDictionary<string, Func<string, IntPtr>> Resolvers = new();
    private static int _registered;

    public static void Register(string libraryName, Func<string, IntPtr> resolve)
    {
        Resolvers[libraryName] = resolve;
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        Resolvers.TryGetValue(libraryName, out Func<string, IntPtr>? resolve) ? resolve(libraryName) : IntPtr.Zero;
}
