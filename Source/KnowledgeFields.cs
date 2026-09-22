using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
namespace Neko.RimWorld;

// Materializes the loaded Def object graph without invoking properties, getters,
// ToString overrides or arbitrary enumerators. This is a source snapshot, not the
// model-facing response: its limits protect the game process and are deliberately
// much higher than the wiki response limit.
internal sealed class KnowledgeFields
{
    // Resource guard, not a presentation limit. Every limit hit is incomplete.
    internal const int MaxDepth = 128;
    internal const int MaxFieldsPerObject = 8192;
    internal const int MaxItemsPerCollection = 1000000;
    internal const int MaxFacts = 200000;
    internal const int MaxNodes = 2000000;
    internal const int MaxDiagnosticsPerEntry = 128;

    private readonly Func<object, KnowledgeRelated?> defReference;
    private readonly Func<Type, bool> forbiddenType;
    private readonly Dictionary<object, string> seen = new Dictionary<object, string>(ReferenceEquality.Instance);
    private KnowledgeEntry entry = null!;
    private int nodes;

    internal KnowledgeFields(Func<object, KnowledgeRelated?> defReference, Func<Type, bool> forbiddenType)
    {
        this.defReference = defReference;
        this.forbiddenType = forbiddenType;
    }

    internal void Extract(object definition, KnowledgeEntry target)
    {
        entry = target;
        nodes = 0;
        seen.Clear();
        seen.Add(definition, "$");
        try { VisitObject(definition, "", 0); }
        finally
        {
            seen.Clear();
            entry = null!;
        }
    }

    private void VisitObject(object value, string path, int depth)
    {
        var fields = new List<FieldInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var type = value.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsLiteral || field.IsNotSerialized || field.Name.StartsWith("<", StringComparison.Ordinal) ||
                    path.Length == 0 && field.Name == "modContentPack" ||
                    path.Length == 0 && (field.Name == "defName" || field.Name == "label" || field.Name == "description"))
                    continue;
                if (HasUnsavedAttribute(field) || !names.Add(field.Name)) continue;
                fields.Add(field);
            }
        }

        fields.Sort((left, right) =>
        {
            var declaring = StringComparer.Ordinal.Compare(left.DeclaringType?.FullName, right.DeclaringType?.FullName);
            return declaring != 0 ? declaring : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });

        var count = 0;
        foreach (var field in fields)
        {
            if (++count > MaxFieldsPerObject)
            {
                Incomplete(path, "field limit");
                return;
            }
            if (!CanVisit())
            {
                Incomplete(path, "node limit");
                return;
            }

            var fieldPath = path.Length == 0 ? field.Name : path + "." + field.Name;
            try
            {
                Visit(field.GetValue(value), fieldPath, depth + 1, field.FieldType);
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                Incomplete(fieldPath, "field read failed: " + exception.GetType().Name);
            }
        }
    }

    private void Visit(object? value, string path, int depth, Type? declaredType)
    {
        if (!CanVisit())
        {
            Incomplete(path, "node limit");
            return;
        }
        if (value == null)
        {
            Fact(path, "null", "null");
            return;
        }

        var type = value.GetType();
        if (value is string text)
        {
            Fact(path, text, "System.String");
            return;
        }
        if (value is Type representedType)
        {
            Fact(path, representedType.FullName ?? representedType.Name, "System.Type");
            return;
        }
        if (type.IsEnum)
        {
            Fact(path, Enum.Format(type, value, "G"), type.FullName);
            return;
        }
        if (type.IsPrimitive || value is decimal || value is DateTime || value is TimeSpan)
        {
            Fact(path, ScalarText(value), type.FullName);
            return;
        }

        var reference = defReference(value);
        if (reference != null)
        {
            Fact(path, reference.Key, type.FullName);
            AddRelated(reference);
            return;
        }

        // These are deliberately omitted from a Def snapshot. They are runtime
        // graphs or executable/resource objects, not loaded definition data.
        if (forbiddenType(type) || value is Delegate || value is MemberInfo)
        {
            ExpectedOmission(path, type.FullName ?? type.Name);
            return;
        }

        if (!type.IsValueType)
        {
            if (seen.TryGetValue(value, out var target))
            {
                Fact(path + ".$ref", target);
                return;
            }
            seen.Add(value, path);
        }
        if (depth > MaxDepth)
        {
            Incomplete(path, "depth limit");
            if (!type.IsValueType) seen.Remove(value);
            return;
        }

        try
        {
            Fact(path + ".$type", type.FullName ?? type.Name);
            if (value is IDictionary dictionary && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                VisitDictionary(dictionary, path, depth);
                return;
            }
            if (IsHashSet(type))
            {
                VisitKnownEnumerable((IEnumerable)value, path, depth);
                return;
            }
            if (value is Array array)
            {
                VisitArray(array, path, depth);
                return;
            }
            if (value is IList list && IsList(type))
            {
                VisitList(list, path, depth);
                return;
            }
            // Custom collections (including curves) are data objects too.
            // Read their fields, never invoke their custom iterator.
            VisitObject(value, path, depth);
        }
        finally
        {
            if (!type.IsValueType)
                seen.Remove(value);
        }
    }

    private void VisitArray(Array array, string path, int depth)
    {
        Fact(path + ".count", array.Length.ToString(CultureInfo.InvariantCulture));
        if (array.Length > MaxItemsPerCollection)
            Incomplete(path, "collection item limit");
        var count = Math.Min(array.Length, MaxItemsPerCollection);
        var indices = new int[array.Rank];
        for (var index = 0; index < count; index++)
        {
            var remainder = index;
            for (var dimension = array.Rank - 1; dimension >= 0; dimension--)
            {
                indices[dimension] = array.GetLowerBound(dimension) + remainder % array.GetLength(dimension);
                remainder /= array.GetLength(dimension);
            }
            Visit(array.GetValue(indices), path + "[" + string.Join(",", indices) + "]", depth + 1, array.GetType().GetElementType());
        }
    }

    private void VisitList(IList list, string path, int depth)
    {
        Fact(path + ".count", list.Count.ToString(CultureInfo.InvariantCulture));
        if (list.Count > MaxItemsPerCollection)
            Incomplete(path, "collection item limit");
        var count = Math.Min(list.Count, MaxItemsPerCollection);
        var declaredType = list.GetType().GetGenericArguments()[0];
        for (var index = 0; index < count; index++)
            Visit(list[index], path + "[" + index + "]", depth + 1, declaredType);
    }

    private void VisitKnownEnumerable(IEnumerable values, string path, int depth)
    {
        // Only exact HashSet<T> reaches this method; use its framework Count,
        // never call a user-defined property or report a truncated count as total.
        var total = (int)values.GetType().GetProperty("Count")!.GetValue(values, null)!;
        var index = 0;
        foreach (var value in values)
        {
            if (index >= MaxItemsPerCollection)
            {
                Incomplete(path, "collection item limit");
                break;
            }
            Visit(value, path + "[" + index + "]", depth + 1, null);
            index++;
        }
        Fact(path + ".count", total.ToString(CultureInfo.InvariantCulture));
    }

    private void VisitDictionary(IDictionary dictionary, string path, int depth)
    {
        Fact(path + ".count", dictionary.Count.ToString(CultureInfo.InvariantCulture));
        if (dictionary.Count > MaxItemsPerCollection)
            Incomplete(path, "dictionary item limit");

        var rows = new List<DictionaryEntry>();
        foreach (DictionaryEntry row in dictionary)
        {
            if (rows.Count >= MaxItemsPerCollection) break;
            rows.Add(row);
        }
        // Indexed key/value pairs preserve key types and avoid collisions such
        // as integer 1 vs string "1", or field punctuation in a string key.
        for (var i = 0; i < rows.Count; i++)
        {
            Visit(rows[i].Key, path + "[" + i + "].key", depth + 1, null);
            Visit(rows[i].Value, path + "[" + i + "].value", depth + 1, null);
        }
    }

    private void AddRelated(KnowledgeRelated reference)
    {
        if (entry.Related.Exists(item => item.Key == reference.Key)) return;
        entry.Related.Add(reference);
    }

    private bool CanVisit()
    {
        nodes++;
        if (nodes <= MaxNodes && entry.Facts.Count < MaxFacts) return true;
        Incomplete("", "knowledge traversal limit");
        return false;
    }

    private void Fact(string name, string value, string? type = null)
    {
        if (entry.Facts.Count >= MaxFacts)
        {
            Incomplete(name, "fact limit");
            return;
        }
        entry.Facts.Add(new KnowledgeFact { Name = name, Value = value, Type = type });
    }

    private void ExpectedOmission(string path, string reason) => entry.AddDiagnostic(path, "expected_omission", reason);

    private void Incomplete(string path, string reason)
    {
        entry.Truncated = true;
        entry.AddDiagnostic(path, "incomplete", reason);
    }

    private static bool IsList(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    private static bool IsHashSet(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>);

    private static bool HasUnsavedAttribute(FieldInfo field)
    {
        foreach (var attribute in CustomAttributeData.GetCustomAttributes(field))
        {
            if (attribute.AttributeType.FullName != "Verse.UnsavedAttribute") continue;
            // Do not instantiate attributes. RimWorld permits XML loading when
            // Unsaved.allowLoading is true despite excluding save-game storage.
            var allowLoading = false;
            var parameters = attribute.Constructor.GetParameters();
            for (var i = 0; i < parameters.Length && i < attribute.ConstructorArguments.Count; i++)
                if (parameters[i].Name == "allowLoading") allowLoading = attribute.ConstructorArguments[i].Value is bool flag && flag;
            foreach (var argument in attribute.NamedArguments)
                if (argument.MemberName == "allowLoading") allowLoading = argument.TypedValue.Value is bool flag && flag;
            return !allowLoading;
        }
        return false;
    }

    private static string ScalarText(object value)
    {
        if (value is bool boolean) return boolean ? "true" : "false";
        if (value is char character) return character.ToString();
        if (value is float single) return single.ToString("G9", CultureInfo.InvariantCulture);
        if (value is double number) return number.ToString("G17", CultureInfo.InvariantCulture);
        if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";
        return value.GetType().FullName ?? value.GetType().Name;
    }

    internal static bool Recoverable(Exception exception) => exception is not OutOfMemoryException &&
        exception is not AccessViolationException && exception is not System.Threading.ThreadAbortException;

    private sealed class ReferenceEquality : IEqualityComparer<object>
    {
        internal static readonly ReferenceEquality Instance = new ReferenceEquality();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
