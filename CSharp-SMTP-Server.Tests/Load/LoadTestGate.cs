namespace CSharp_SMTP_Server.Tests.Load;

/// <summary>
/// Opt-in gate for the heavy load tier.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier runs on every <c>dotnet test</c> and is a normal correctness test: modest scale,
/// deterministic assertions, a few seconds of wall clock. The heavy tier (the full ladder up to
/// 1000+ messages) runs only when <c>SMTP_LOADTEST=1</c>, because the suite is otherwise ~11 s and a
/// multi-minute load run on every build trains people to stop running tests.
/// </para>
/// <para>
/// xUnit 2.4 has no first-class "skip at runtime" API, so gated facts assert nothing and return
/// early when disabled. They are additionally tagged <c>[Trait("Load", "heavy")]</c> so they can be
/// excluded or selected by filter: <c>dotnet test --filter "Load=heavy"</c>.
/// </para>
/// </remarks>
internal static class LoadTestGate
{
    /// <summary>Whether the heavy tier is enabled for this run.</summary>
    internal static bool HeavyEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("SMTP_LOADTEST");
            return value is "1" or "true" or "TRUE" or "yes";
        }
    }

    /// <summary>
    /// Returns true and prints a note when the heavy tier is disabled, so the caller can return early.
    /// </summary>
    internal static bool SkipHeavy(string scenario)
    {
        if (HeavyEnabled) return false;

        Console.WriteLine($"[load] skipping heavy scenario '{scenario}' (set SMTP_LOADTEST=1 to enable)");
        return true;
    }
}
