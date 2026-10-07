using System;
using System.Linq;

namespace OnlyBebop;

/// systems.launch_args: Melty starts us with --deadlock "{game}". --onlyup overrides where Only Up! is.
public sealed record LaunchArgs(string? DeadlockDir, string? OnlyUpDir, bool SelfTest)
{
    public static LaunchArgs Parse(string[] args)
    {
        string? Get(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var deadlock = Get("--deadlock") ?? Environment.GetEnvironmentVariable("ONLYBEBOP_DEADLOCK");
        return new LaunchArgs(
            string.IsNullOrWhiteSpace(deadlock) ? null : deadlock.Trim('"'),
            Get("--onlyup")?.Trim('"'),
            args.Contains("--selftest"));
    }
}
