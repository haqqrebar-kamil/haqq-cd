// Minimal, source-compatible stand-in for the parts of xUnit used by HAQQ.CAD.Tests.
// It exists only so the test sources can be executed on a machine without NuGet access.
// `dotnet test` on tests/HAQQ.CAD.Tests uses the real xUnit and is the canonical run.
using System.Collections;

namespace Xunit;

[AttributeUsage(AttributeTargets.Method)]
public class FactAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class TheoryAttribute : FactAttribute { }

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute : Attribute
{
    public InlineDataAttribute(params object?[] data) => Data = data;
    public object?[] Data { get; }
}

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

public static class Assert
{
    private static void Fail(string message) => throw new AssertionException(message);

    public static void True(bool condition, string? message = null)
    {
        if (!condition) Fail(message ?? "Expected true but was false.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) Fail(message ?? "Expected false but was true.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (expected is IEnumerable e && actual is IEnumerable a && expected is not string)
        {
            var el = e.Cast<object?>().ToList();
            var al = a.Cast<object?>().ToList();
            if (el.Count != al.Count || el.Where((x, i) => !Equals(x, al[i])).Any())
                Fail($"Sequences differ. Expected [{string.Join(", ", el)}] but was [{string.Join(", ", al)}].");
            return;
        }
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) Fail($"Expected <{expected}> but was <{actual}>.");
    }

    public static void Equal(double expected, double actual, int precision)
    {
        if (Math.Round(expected, precision) != Math.Round(actual, precision))
            Fail($"Expected <{expected:R}> but was <{actual:R}> (precision {precision}).");
    }

    public static void NotEqual<T>(T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) Fail($"Expected a value different from <{expected}>.");
    }

    public static void Null(object? value)
    {
        if (value is not null) Fail($"Expected null but was <{value}>.");
    }

    public static void NotNull(object? value)
    {
        if (value is null) Fail("Expected a non-null value.");
    }

    public static void Same(object? expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual)) Fail("Expected the same instance.");
    }

    public static void NotSame(object? expected, object? actual)
    {
        if (ReferenceEquals(expected, actual)) Fail("Expected different instances.");
    }

    public static void Empty(IEnumerable collection)
    {
        int n = collection.Cast<object?>().Count();
        if (n != 0) Fail($"Expected an empty collection but it had {n} item(s).");
    }

    public static void NotEmpty(IEnumerable collection)
    {
        if (!collection.Cast<object?>().Any()) Fail("Expected a non-empty collection.");
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        var list = collection.ToList();
        if (list.Count != 1) Fail($"Expected exactly one item but found {list.Count}.");
        return list[0];
    }

    public static void Contains(string expectedSubstring, string? actual)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
            Fail($"Expected text containing \"{expectedSubstring}\" but was \"{actual}\".");
    }

    public static void DoesNotContain(string unexpectedSubstring, string? actual)
    {
        if (actual is not null && actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
            Fail($"Did not expect text containing \"{unexpectedSubstring}\".");
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection)
    {
        if (!collection.Contains(expected)) Fail($"Expected the collection to contain <{expected}>.");
    }

    public static void DoesNotContain<T>(T unexpected, IEnumerable<T> collection)
    {
        if (collection.Contains(unexpected)) Fail($"Did not expect the collection to contain <{unexpected}>.");
    }

    public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        if (!collection.Any(x => filter(x))) Fail("Expected an item matching the filter.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        if (collection.Any(x => filter(x))) Fail("Did not expect an item matching the filter.");
    }

    public static void InRange<T>(T actual, T low, T high) where T : IComparable
    {
        if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0) Fail($"Expected <{actual}> to be in [{low}, {high}].");
    }

    public static T IsType<T>(object? value)
    {
        if (value is null || value.GetType() != typeof(T)) Fail($"Expected type {typeof(T).Name} but was {value?.GetType().Name ?? "null"}.");
        return (T)value!;
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (ex.GetType() == typeof(T)) return (T)ex;
            Fail($"Expected {typeof(T).Name} but {ex.GetType().Name} was thrown: {ex.Message}");
        }
        Fail($"Expected {typeof(T).Name} but nothing was thrown.");
        return null!;
    }
}
