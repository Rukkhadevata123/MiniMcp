using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

// Shared assertion helpers for the standalone checks executables. Every check file sees these
// unqualified through `global using static CheckAssertions;` (GlobalUsings.cs).
internal static partial class CheckAssertions
{
    /// <summary>
    /// Fails with <paramref name="message"/> and the asserted expression text, so a failed
    /// `Assert(a == b &amp;&amp; c)` reports which condition was checked even without a message.
    /// </summary>
    public static void Assert(
        bool condition,
        string? message = null,
        [CallerArgumentExpression(nameof(condition))] string expression = "")
    {
        if (condition)
            return;

        var asserted = Whitespace().Replace(expression, " ");
        throw new Exception(message is null
            ? $"Assertion failed: {asserted}"
            : $"{message} (asserted: {asserted})");
    }

    public static T Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }

        throw new Exception($"Expected {typeof(T).Name}.");
    }

    // Overload resolution prefers this over the Action overload for any lambda whose body is an
    // expression (including `() => throw ...`), so callers must await the result. Checks run inside
    // async lambdas, where an un-awaited call is a CS4014 warning.
    public static async Task<T> Reject<T>(Func<Task> action, TimeSpan? timeout = null) where T : Exception
    {
        try
        {
            var pending = action();
            await (timeout is { } limit ? pending.WaitAsync(limit) : pending);
        }
        catch (T error)
        {
            return error;
        }

        throw new Exception($"Expected {typeof(T).Name}.");
    }

    public static Task<T> Reject<T>(Task task, TimeSpan? timeout = null) where T : Exception =>
        Reject<T>(() => task, timeout);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
