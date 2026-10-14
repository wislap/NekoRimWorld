using System;
using System.Collections;
using System.Linq;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Argument limits are shared by typed native fields and public query schemas.</summary>
internal static class GameArguments
{
    public const int MaxIds = 50;
    public const int MaxNameLength = 128;

    public static QueryArgument Ids(string name) => Create(name,
        new ListGraphType(new NonNullGraphType(new IdGraphType())),
        "Exact IDs; at most " + MaxIds + ". Empty list matches nothing.",
        new JObject { ["maxItems"] = MaxIds });
    public static QueryArgument Create(string name, IGraphType type, string description,
        JObject? limits = null, object? defaultValue = null)
    {
        var constraints = limits ?? new JObject();
        var argument = new QueryArgument(type) { Name = name, Description = description, DefaultValue = defaultValue };
        argument.Metadata["json_limits"] = constraints;
        argument.Validator = value => {
            if (value == null) return;
            if (constraints["minimum"] != null && Convert.ToInt64(value) < (long)constraints["minimum"]!)
                throw new ArgumentException(name + " is below its minimum " + constraints["minimum"] + ".");
            if (constraints["maximum"] != null && Convert.ToInt64(value) > (long)constraints["maximum"]!)
                throw new ArgumentException(name + " exceeds its maximum " + constraints["maximum"] + ".");
            if (constraints["maxLength"] != null && value is string text && text.Length > (int)constraints["maxLength"]!)
                throw new ArgumentException(name + " exceeds its text limit.");
            if (constraints["minItems"] != null && value is IEnumerable minimumValues && minimumValues.Cast<object>().Count() < (int)constraints["minItems"]!)
                throw new ArgumentException(name + " requires at least " + constraints["minItems"] + " items.");
            if (constraints["items"]?["enum"] is JArray choices && value is IEnumerable choicesValues &&
                choicesValues.Cast<object>().Any(item => !choices.Any(choice => JToken.DeepEquals(choice, JToken.FromObject(item)))))
                throw new ArgumentException(name + " contains an unsupported choice.");
            if (constraints["maxItems"] != null && value is IEnumerable values && values.Cast<object>().Count() > (int)constraints["maxItems"]!)
                throw new ArgumentException(name + " exceeds its list limit.");
        };
        return argument;
    }
}
