using System;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ColonyReadContractTests
{
    internal static void Run()
    {
        var catalog = ColonyReadContract.Catalog();
        Require((string?)catalog["version"] == "colony_state.v1" && ((JArray)catalog["operations"]!).Count == 7, "Complete additive native business catalog");
        Require(catalog["tools"] == null, "Native operations do not become seven extra public tools");
        ColonyReadContract.Validate("inspect_research", JObject.Parse("{\"names\":[\"MicroelectronicsBasics\"],\"aspects\":[\"progress\",\"requirements\"]}"));
        ColonyReadContract.Validate("evaluate_journey", JObject.Parse("{\"caravan\":{\"name\":\"采购队\"},\"destinations\":[{\"name\":\"据点\"}]}"));
        Invalid(() => ColonyReadContract.Validate("inspect_research", JObject.Parse("{\"names\":[]}")), "invalid_arguments");
        Invalid(() => ColonyReadContract.Validate("inspect_quests", JObject.Parse("{\"limit\":0}")), "invalid_arguments");
        Invalid(() => ColonyReadContract.Validate("inspect_quests", JObject.Parse("{\"fields\":[\"all\"]}")), "invalid_arguments");
        var population = new[] { new { Id = "a", Name = "Twin", Active = true }, new { Id = "b", Name = "Twin", Active = false }, new { Id = "c", Name = "Other", Active = false } };
        Require(ColonyReadContract.Select(population, new JObject(), x => x.Id, x => x.Name, x => x.Active).Single().Id == "a", "Default public population");
        Require(ColonyReadContract.Select(population, JObject.Parse("{\"names\":[\"other\"]}"), x => x.Id, x => x.Name, x => x.Active).Single().Id == "c", "Direct named history lookup, no prerequisite directory read");
        Invalid(() => ColonyReadContract.Select(population, JObject.Parse("{\"names\":[\"Twin\"]}"), x => x.Id, x => x.Name), "ambiguous_reference");
        Require(ColonyReadContract.Select(population, JObject.Parse("{\"ids\":[\"b\"]}"), x => x.Id, x => x.Name).Single().Id == "b", "Exact instance identity disambiguates");
        var page = ColonyReadContract.Page(JObject.Parse("{\"offset\":1,\"limit\":1}"), population.Select(x => new JObject { ["id"] = x.Id }), "declared", new SessionDescriptor("colony", "load", 12, 3));
        Require((int)page["total"]! == 3 && (string?)page["rows"]![0]!["id"] == "b" && (bool)page["page"]!["has_more"]!, "Page preserves pre-page total and continuation");
        Require((int?)page["page"]!["next_offset"] == 2, "Continuation is directly callable");
        var end = ColonyReadContract.Page(JObject.Parse("{\"offset\":2,\"limit\":1}"), population.Select(x => new JObject { ["id"] = x.Id }), "declared", new SessionDescriptor("colony", "load", 12, 3));
        Require(!(bool)end["page"]!["has_more"]! && end["page"]!["next_offset"]!.Type == JTokenType.Null, "Final page has no continuation");
        Require((string?)page["sample"]!["colony_id"] == "colony" && !(bool)page["sample"]!["atomic"]!, "Read preserves sample and avoids atomic claims");
        var export = Environment.GetEnvironmentVariable("NEKO_COLONY_CATALOG_EXPORT");
        if (!string.IsNullOrEmpty(export)) System.IO.File.WriteAllText(export, catalog.ToString());
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Invalid(Action action, string code)
    {
        try { action(); throw new Exception("Invalid request accepted: " + code); }
        catch (ExecutionError error) when (error.Code == code) { }
    }
}
