using System;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class SituationContractTests
{
    internal static void Run()
    {
        void Good(string input) => SituationContract.Validate(JObject.Parse(input));
        void Bad(string input) {
            try { Good(input); } catch (ExecutionError e) when (e.Code == "invalid_arguments") { return; }
            throw new Exception("Invalid situation arguments accepted: " + input);
        }
        Good("{}");
        Good("{'map':'current'}");
        Bad("{'map':''}");
        Bad("{'map':3}");
        Bad("{'limit':20}");
        Bad("{'window_ticks':120000}");
        var catalog = SituationContract.Catalog();
        if ((string?)catalog["operation"] != "read_situation" || (int?)catalog["event_window_ticks"] != 60000)
            throw new Exception("Situation catalog must advertise read_situation with a one-day event window");
        var properties = ((JObject)catalog["parameters"]!["properties"]!).Properties().Select(p => p.Name).ToArray();
        if (!properties.SequenceEqual(new[] { "map" }))
            throw new Exception("Situation must not expose caps, windows or filters as arguments");
        Console.WriteLine("Situation contract tests passed: map-only arguments, no caps, one-day window.");
    }
}
