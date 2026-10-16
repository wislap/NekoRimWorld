using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class ResearchReadings
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    // GetProgress and its IsFinished/ProgressReal callers insert missing keys.
    // Read the version-bound dictionaries instead, preserving native zero defaults.
    private static T State<T>(ResearchManager manager, string name) => (T)typeof(ResearchManager).GetField(name, Fields)!.GetValue(manager)!;
    internal static JObject Read(JObject args, SessionDescriptor session)
    {
        var aspects = ColonyReadContract.Aspects(args, "progress");
        ColonyReadContract.RequireAspects(aspects, "progress", "requirements", "unlocks");
        var manager = Find.ResearchManager;
        var progress = State<Dictionary<ResearchProjectDef, float>>(manager, "progress");
        var knowledge = State<Dictionary<ResearchProjectDef, float>>(manager, "anomalyKnowledge");
        var techprints = State<Dictionary<ResearchProjectDef, int>>(manager, "techprints");
        var selected = new HashSet<ResearchProjectDef>();
        var regular = State<ResearchProjectDef?>(manager, "currentProj"); if (regular != null) selected.Add(regular);
        var anomaly = State<List<ResearchManager.KnowledgeCategoryProject>?>(manager, "currentAnomalyKnowledgeProjects");
        if (anomaly != null) foreach (var project in anomaly) if (project.project != null) selected.Add(project.project);
        float Progress(ResearchProjectDef project) => (project.baseCost > 0 ? progress : knowledge).TryGetValue(project, out var value) ? value : 0;
        bool Finished(ResearchProjectDef project) => Progress(project) >= project.Cost;
        bool Visible(ResearchProjectDef project) => Finished(project) || !ModsConfig.AnomalyActive || !Find.EntityCodex.Hidden(project);
        var itemNames = ColonyReadContract.Texts(args, "items");
        if (itemNames.Length > 0 && ColonyReadContract.Targeted(args)) throw ReadContext.Error("invalid_arguments", "Research items and project ids/names are alternative selectors.");
        var goalProjects = new HashSet<ResearchProjectDef>();
        var productionPaths = new JArray();
        foreach (string itemName in itemNames)
        {
            var item = DefDatabase<ThingDef>.GetNamedSilentFail(itemName);
            if (item == null)
            {
                var matches = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => string.Equals(d.label, itemName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw ReadContext.Error(matches.Length > 1 ? "ambiguous_reference" : "object_unavailable", "Research product must identify one loaded definition: " + itemName);
                item = matches[0];
            }
            var recipes = new JArray();
            foreach (var recipe in DefDatabase<RecipeDef>.AllDefsListForReading.Where(r => r.products?.Any(p => p.thingDef == item) == true))
            {
                // Read direct loaded references, avoiding ThingDef.AllRecipes and its lazy cache.
                var benches = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.recipes?.Contains(recipe) == true || recipe.recipeUsers?.Contains(d) == true).ToArray();
                var research = new[] { recipe.researchPrerequisite }.Where(r => r != null).Cast<ResearchProjectDef>()
                    .Concat(recipe.researchPrerequisites ?? new List<ResearchProjectDef>())
                    .Concat(benches.SelectMany(b => b.researchPrerequisites ?? new List<ResearchProjectDef>())).Where(Visible).Distinct().ToArray();
                foreach (var project in research) goalProjects.Add(project);
                recipes.Add(new JObject { ["recipe"] = Definition(recipe),
                    ["products"] = new JArray(recipe.products.Select(product => { var value = Definition(product.thingDef)!; value["units"] = product.count; return value; })),
                    ["workbenches"] = new JArray(benches.Select(bench => { var value = Definition(bench)!;
                        value["research"] = new JArray((bench.researchPrerequisites ?? new List<ResearchProjectDef>()).Where(Visible).Select(Definition));
                        value["existing_player_buildings"] = new JArray(Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).Where(b => b.def == bench).Select(BuildingIdentity)); return value; })),
                    ["research"] = new JArray(research.Select(Definition)) });
            }
            productionPaths.Add(new JObject { ["item"] = Definition(item), ["recipes"] = recipes,
                ["coverage"] = "Direct loaded recipe products, recipe users and workbench research references. Not a claim that ingredients, workers or every custom production mechanism are available." });
        }
        var projects = itemNames.Length > 0 ? goalProjects.OrderBy(p => p.defName, StringComparer.Ordinal).ToArray() : ColonyReadContract.Select(DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(Visible), args,
            p => p.defName, p => p.label, p => selected.Contains(p));
        var rows = projects.Select(project => {
            var row = Identity(project); float real = Progress(project), factor = project.CostFactor(Faction.OfPlayer.def.techLevel);
            if (aspects.Contains("progress")) row["progress"] = new JObject { ["current"] = selected.Contains(project), ["finished"] = Finished(project),
                ["category"] = project.knowledgeCategory?.defName ?? "research", ["real"] = real, ["real_cost"] = project.Cost,
                ["apparent"] = real * factor, ["apparent_cost"] = project.Cost * factor, ["apparent_cost_factor"] = factor,
                ["unit"] = project.baseCost > 0 ? "research_points" : "knowledge", ["fraction"] = project.Cost > 0 ? System.Math.Min(1, real / project.Cost) : 1 };
            if (aspects.Contains("requirements")) {
                var chain = new List<ResearchProjectDef>(); var visited = new HashSet<ResearchProjectDef>();
                void Visit(ResearchProjectDef p) { if (!visited.Add(p)) return; foreach (var prerequisite in Prerequisites(p)) Visit(prerequisite); if (p != project) chain.Add(p); }
                Visit(project);
                var blockers = new JArray(); var direct = Prerequisites(project).ToArray();
                if (direct.Any(p => !Finished(p))) blockers.Add("prerequisites_incomplete");
                int applied = techprints.TryGetValue(project, out var count) ? count : 0;
                if (applied < project.TechprintCount) blockers.Add("techprints_missing");
                var benches = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_ResearchBench>().ToArray();
                var suitable = benches.Where(b => project.CanBeResearchedAt(b, ignoreResearchBenchPowerStatus: true)).ToArray();
                var powered = suitable.Where(b => project.CanBeResearchedAt(b, ignoreResearchBenchPowerStatus: false)).ToArray();
                bool benchRequirement = project.requiredResearchBuilding == null || suitable.Length > 0;
                if (!benchRequirement) blockers.Add("required_research_bench_or_facilities_missing");
                bool mechanitor = project.PlayerMechanitorRequirementMet, analysis = project.AnalyzedThingsRequirementsMet, inspection = project.InspectionRequirementsMet;
                if (!mechanitor) blockers.Add("mechanitor_required"); if (!analysis) blockers.Add("analysis_incomplete"); if (!inspection) blockers.Add("grav_engine_inspection_required");
                row["requirements"] = new JObject { ["can_start_now"] = !Finished(project) && blockers.Count == 0,
                    ["blockers"] = blockers, ["direct_prerequisites"] = new JArray(direct.Select(p => { var identity = Identity(p); identity["finished"] = Finished(p); return identity; })),
                    ["prerequisite_chain"] = new JArray(chain.Select(p => { var identity = Identity(p); identity["finished"] = Finished(p); return identity; })),
                    ["techprints_required"] = project.TechprintCount, ["techprints_applied"] = applied,
                    ["research_building"] = Definition(project.requiredResearchBuilding), ["research_facilities"] = new JArray((project.requiredResearchFacilities ?? new List<ThingDef>()).Select(Definition)),
                    ["suitable_benches"] = new JArray(suitable.Select(b => new JObject { ["id"] = b.GetUniqueLoadID(), ["name"] = b.LabelShort, ["map_id"] = b.Map.GetUniqueLoadID(), ["powered_eligible"] = powered.Contains(b) })),
                    ["bench_components"] = new JObject {
                        ["matching_buildings"] = new JArray(benches.Where(b => project.requiredResearchBuilding == null || b.def == project.requiredResearchBuilding).Select(b => {
                            var value = BuildingIdentity(b); var comp = b.TryGetComp<CompAffectedByFacilities>();
                            value["power_on"] = b.GetComp<CompPowerTrader>()?.PowerOn ?? true;
                            value["compatible_ignoring_bench_power"] = suitable.Contains(b);
                            value["required_facilities"] = new JArray((project.requiredResearchFacilities ?? new List<ThingDef>()).Select(facility => {
                                var status = Definition(facility)!;
                                status["existing_on_map"] = b.Map.listerBuildings.allBuildingsColonist.Count(t => t.def == facility);
                                var linked = comp?.LinkedFacilitiesListForReading.Where(t => t.def == facility).ToArray() ?? Array.Empty<Thing>();
                                status["linked"] = linked.Length; status["active_linked"] = comp == null ? 0 : linked.Count(comp.IsFacilityActive); return status;
                            })); return value;
                        })),
                        ["meaning"] = "A building may exist while its required active linked facility combination is missing. Empty suitable_benches does not imply the building is absent." },
                    ["bench_scope"] = "Colonist research benches on loaded maps, native project compatibility and active linked facilities; power is separate from project selection.",
                    ["work_status"] = "No individual researcher's current work was inspected." };
            }
            if (aspects.Contains("unlocks")) {
                // Same direct reference families as native UnlockedDefs, without
                // filling its lazy cache or assuming custom effects are enumerated.
                var unlocks = new List<JObject>();
                foreach (var recipe in DefDatabase<RecipeDef>.AllDefsListForReading.Where(r => r.researchPrerequisite == project || r.researchPrerequisites != null && r.researchPrerequisites.Contains(project))) {
                    var identity = Definition(recipe)!; identity["kind"] = "recipe"; unlocks.Add(identity);
                }
                foreach (var thing in DefDatabase<ThingDef>.AllDefsListForReading.Where(t => t.researchPrerequisites != null && t.researchPrerequisites.Contains(project) || t.plant?.sowResearchPrerequisites != null && t.plant.sowResearchPrerequisites.Contains(project))) {
                    var identity = Definition(thing)!; identity["kind"] = "thing"; unlocks.Add(identity);
                }
                foreach (var terrain in DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => t.researchPrerequisites != null && t.researchPrerequisites.Contains(project))) {
                    var identity = Definition(terrain)!; identity["kind"] = "terrain"; unlocks.Add(identity);
                }
                row["unlocks"] = new JArray(unlocks.OrderBy(x => (string?)x["kind"]).ThenBy(x => (string?)x["def"]));
                row["custom_unlock_texts"] = new JArray(project.customUnlockTexts ?? new List<string>());
                row["unlocks_coverage"] = "Direct loaded recipe/thing/plant/terrain definition references and custom public unlock texts; not a proof that production or every custom effect is available.";
            }
            return row;
        });
        var result = ColonyReadContract.Page(args, rows, itemNames.Length > 0 ? "Research connected to selected products through direct loaded recipes/workbenches." : ColonyReadContract.Targeted(args) ? "Selected visible loaded research definitions and actual player research state." : "Current regular and knowledge-category projects; no project selected is an empty result.", session);
        if (itemNames.Length > 0) result["production_paths"] = productionPaths;
        return result;
    }
    private static JObject BuildingIdentity(Thing thing) => new() { ["id"] = thing.GetUniqueLoadID(), ["name"] = thing.LabelShort, ["def"] = thing.def.defName, ["map_id"] = thing.Map.GetUniqueLoadID() };
    private static IEnumerable<ResearchProjectDef> Prerequisites(ResearchProjectDef project) => (project.prerequisites ?? new List<ResearchProjectDef>()).Concat(project.hiddenPrerequisites ?? new List<ResearchProjectDef>()).Distinct();
    private static JObject Identity(ResearchProjectDef project) => new() { ["id"] = project.defName, ["def"] = project.defName, ["name"] = project.label };
    private static JObject? Definition(Def? def) => def == null ? null : new JObject { ["def"] = def.defName, ["name"] = def.label };
}
