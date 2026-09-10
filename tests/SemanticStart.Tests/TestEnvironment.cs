using System.Runtime.CompilerServices;
using SemanticStart.Core;

namespace SemanticStart.Tests;

/// <summary>
/// Redirects every on-disk location the product uses into a scratch directory for the duration of
/// a test run.
///
/// <see cref="AppPaths"/> has always documented <c>SEMANTICSTART_HOME</c> as the way tests keep off
/// the real index, but nothing set it, so the isolation was documented rather than applied. The
/// visible cost was in the log: <c>IndexRebuildCoordinatorTests</c> drives the rebuild failure path
/// with a deliberately thrown "indexing blew up", and that landed in the shipping
/// <c>%LOCALAPPDATA%\SemanticStart\logs\app.log</c> as a genuine-looking [ERROR]. A fake error in
/// the log a user is asked to send in is worse than a noisy test: it is a false lead during the one
/// activity the log exists to support.
///
/// A module initializer rather than a fixture, because the ordering requirement is stricter than
/// "before each test". <see cref="AppPaths.Root"/> is a static property initialised on first
/// access and <c>Log</c> caches its path in a static field, so both latch whatever the environment
/// said the first time any code touched them; a collection fixture can run after that has already
/// happened. Module initializers run at assembly load, before any test type is used at all.
///
/// Reading <see cref="AppPaths.HomeVariable"/> here is safe for the same reason it looks risky: it
/// is a const, inlined by the compiler, so naming it does not trigger the static initialisation
/// this has to precede.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "SemanticStart.Tests", Environment.ProcessId.ToString());

        // A fresh root per run. Tests that assert on first-run behaviour would otherwise read
        // state left by the previous run, which is the failure mode that makes a shared scratch
        // directory worse than no isolation at all.
        if (Directory.Exists(root))
            TryDelete(root);

        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
    }

    /// <summary>
    /// Best effort. A scratch directory that outlives its run costs nothing, whereas throwing from
    /// a module initializer or a process-exit handler takes the whole test run with it.
    /// </summary>
    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
