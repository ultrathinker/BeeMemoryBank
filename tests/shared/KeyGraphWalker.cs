using System.Reflection;
using System.Text;

namespace BeeMemoryBank.TestSupport;

/// <summary>Where a key was found: the path of fields from a named root, and what held it.</summary>
public sealed record KeyHit(string Path, string Kind)
{
    public override string ToString() => $"{Path} ({Kind})";
}

/// <summary>
/// Looks for a secret key in the managed object graph (docs/design/step-b-spec.md 15.1.3): every field of every object reachable from the
/// named roots, arrays and boxed structs included, with a visited set. A hit is a string or char array that contains the key as hex (any
/// case, so the SQLCipher <c>x'&lt;hex&gt;'</c> form is covered), or a byte array that contains the raw key or its ASCII hex.
/// </summary>
public static class KeyGraphWalker
{
    public static List<KeyHit> Find(IEnumerable<(string Name, object? Root)> roots, byte[] key, IEnumerable<object>? doNotEnter = null)
    {
        var hex = Convert.ToHexString(key);
        var hexAscii = Encoding.ASCII.GetBytes(hex.ToLowerInvariant());
        var hits = new List<KeyHit>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (doNotEnter is not null) foreach (var skipped in doNotEnter) seen.Add(skipped); // objects the caller has judged separately

        void Walk(object? o, string path)
        {
            if (o is null) return;
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || t.IsPointer || o is Pointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return;
            if (o is string s)
            {
                if (s.Contains(hex, StringComparison.OrdinalIgnoreCase)) hits.Add(new KeyHit(path, $"string of {s.Length} characters"));
                return;
            }
            if (o is Type || o is Assembly || o is Module || o is MemberInfo || o is Thread || o is System.Threading.CancellationToken) return;
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is byte[] bytes)
            {
                if (bytes.AsSpan().IndexOf(key) >= 0) hits.Add(new KeyHit(path, "raw key bytes"));
                else if (bytes.AsSpan().IndexOf(hexAscii) >= 0) hits.Add(new KeyHit(path, "ASCII hex bytes"));
                return;
            }
            if (o is char[] chars)
            {
                if (new string(chars).Contains(hex, StringComparison.OrdinalIgnoreCase)) hits.Add(new KeyHit(path, "char array"));
                return;
            }
            if (o is Array array)
            {
                if (array.GetType().GetElementType()!.IsPrimitive) return;
                var i = 0;
                foreach (var element in array) Walk(element, $"{path}[{i++}]");
                return;
            }
            for (var type = t; type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsPointer) continue;
                    object? value;
                    try { value = field.GetValue(o); }
                    catch { continue; }
                    Walk(value, $"{path}.{field.Name}");
                }
            }
        }

        foreach (var (name, root) in roots) Walk(root, name);
        return hits;
    }

    /// <summary>
    /// The registry in which Microsoft.Data.Sqlite keeps every connection string it has been given (pool groups, pools, pools waiting to be
    /// released). Found by reflection; if the provider's internals change this throws, and the test is adapted - it is never skipped.
    /// </summary>
    public static object ProviderRegistry()
    {
        var type = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetType("Microsoft.Data.Sqlite.SqliteConnectionFactory")
                   ?? throw new InvalidOperationException("Microsoft.Data.Sqlite.SqliteConnectionFactory is gone: adapt KeyGraphWalker.");
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetField("Instance", all)?.GetValue(null)
               ?? type.GetProperty("Instance", all)?.GetValue(null)
               ?? throw new InvalidOperationException("SqliteConnectionFactory.Instance is gone: adapt KeyGraphWalker.");
    }
}
