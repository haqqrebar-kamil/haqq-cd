using System.Diagnostics;
using System.Reflection;
using Xunit;

// Discovers and runs [Fact]/[Theory] methods compiled into this assembly.
int passed = 0, failed = 0;
var failures = new List<string>();
var clock = Stopwatch.StartNew();

var classes = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
    .OrderBy(t => t.FullName);

foreach (var type in classes)
{
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(m => m.Name))
    {
        if (method.GetCustomAttribute<FactAttribute>() is null) continue;

        var rows = method.GetCustomAttributes<InlineDataAttribute>().Select(a => a.Data).ToList();
        if (rows.Count == 0) rows.Add(Array.Empty<object?>());

        foreach (var row in rows)
        {
            string name = $"{type.Name}.{method.Name}" + (row.Length > 0 ? $"({string.Join(", ", row.Select(v => v ?? "null"))})" : "");
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                var parameters = method.GetParameters();
                var values = new object?[parameters.Length];
                for (int i = 0; i < values.Length; i++)
                    values[i] = row[i] is null ? null : Convert.ChangeType(row[i], Nullable.GetUnderlyingType(parameters[i].ParameterType) ?? parameters[i].ParameterType);
                object? result = method.Invoke(instance, values);
                if (result is Task task) task.GetAwaiter().GetResult();
                passed++;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                failed++;
                failures.Add($"FAIL {name}\n     {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Tests"))}");
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }
    }
}

foreach (string f in failures) Console.WriteLine(f);
Console.WriteLine($"Offline runner: {passed + failed} tests, {passed} passed, {failed} failed, {clock.ElapsedMilliseconds} ms");
return failed == 0 ? 0 : 1;
