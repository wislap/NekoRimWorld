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
        Require((string?)catalog["version"] == "colony_state.v1" && ((JArray)catalog["operations"]!).Count == 8, "Complete additive native business catalog");
        Require(catalog["tools"] == null, "Native operations do not become extra public tools");
        ColonyReadContract.Validate("inspect_research", JObject.Parse("{\"names\":[\"MicroelectronicsBasics\"],\"aspects\":[\"progress\",\"requirements\"]}"));
        ColonyReadContract.Validate("evaluate_journey", JObject.Parse("{\"caravan\":{\"name\":\"采购队\"},\"destinations\":[{\"name\":\"据点\"}]}"));
        ColonyReadContract.Validate("inspect_resources", JObject.Parse("{items:['WoodLog'],map:'current',aspects:['quantity','distribution']}"));
        ColonyReadContract.Validate("inspect_research", JObject.Parse("{items:['ComponentIndustrial'],aspects:['requirements']}"));
        ColonyReadContract.Validate("evaluate_journey", JObject.Parse("{caravan:{name:'采购队'},destinations:[{name:'据点'}],cargo_add:[{item:'WoodLog',quantity:40}]}"));
        Invalid(() => ColonyReadContract.Validate("inspect_resources", new JObject()), "invalid_arguments");
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
        var selection = ColonyReadContract.ExactDefinitionSelection(new[] { "simple meal", "Pemmican" }, new[] { "MealSimple", "Pemmican" });
        var resourcePage = ColonyReadContract.Page(JObject.Parse("{limit:1}"), new[] {
            new JObject { ["def_name"] = "MealSimple", ["observed_units"] = 0, ["coverage"] = "complete_in_scope", ["sources"] = new JArray() },
            new JObject { ["def_name"] = "Pemmican", ["observed_units"] = 0, ["coverage"] = "complete_in_scope", ["sources"] = new JArray() }
        }, new JObject { ["selection"] = selection, ["population"] = "visible_spawned_map_and_real_holder_contents_plus_existing_public_map_trader_stock" }, new SessionDescriptor("colony", "load", 12, 3));
        Require((string?)resourcePage["scope"]!["selection"]!["coverage"] == "selected_definitions_only", "Zero quantities remain scoped to selected definitions, not all food");
        Require(resourcePage["scope"]!["selection"]!["requested"]!.Values<string>().SequenceEqual(new[] { "simple meal", "Pemmican" })
            && resourcePage["scope"]!["selection"]!["resolved_definitions"]!.Values<string>().SequenceEqual(new[] { "MealSimple", "Pemmican" }), "Exact-label resolution preserves requested and native definition identities");
        Require((bool)resourcePage["page"]!["has_more"]! && (int)resourcePage["total"]! == 2
            && resourcePage["scope"]!["selection"]!["resolved_definitions"]!.Count() == 2, "Paged resource rows retain whole selected definition scope without claiming listing completeness");
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
