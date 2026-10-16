using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;

namespace Neko.RimWorld;

// Native instance presentation is gameplay evidence: some conditions format
// gender, intensity or pollution into their label/description. Defs are templates.
internal static class SituationConditionPresentation
{
    private static readonly HashSet<string> AuditedGetters = new(StringComparer.Ordinal) {
        "RimWorld.GameCondition.get_Label", "RimWorld.GameCondition.get_Description",
        "RimWorld.GameCondition_PsychicSuppression.get_Description",
        "RimWorld.GameCondition_HateChantDrone.get_Label",
        "RimWorld.GameCondition_NoxiousHaze.get_Description",
        "RimWorld.GameCondition_PsychicEmanation.get_Label", "RimWorld.GameCondition_PsychicEmanation.get_Description"
    };
    internal static JObject Read(GameCondition condition)
    {
        GameImplementation.RequireKnownBinary();
        var result = new JObject();
        var errors = new JArray();
        var sources = new JObject();
        foreach (var property in new[] { nameof(GameCondition.Label), nameof(GameCondition.Description) }) {
            var key = property.ToLowerInvariant();
            try {
                var getter = condition.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetMethod;
                var symbol = getter?.DeclaringType?.FullName + "." + getter?.Name;
                sources[key] = symbol;
                if (getter == null || getter.DeclaringType?.Assembly != typeof(GameCondition).Assembly || !AuditedGetters.Contains(symbol))
                    throw ReadContext.Error("unsupported_mod_override", "Condition presentation getter has not been audited: " + symbol);
                var patch = Harmony.GetPatchInfo(getter);
                if (patch != null && patch.Owners.Count > 0)
                    throw ReadContext.Error("unsupported_behavior_patch", "Condition presentation getter is patched: " + symbol);
                result[key] = property == nameof(GameCondition.Label) ? condition.Label : condition.Description;
            } catch (Exception error) when (ReadContext.Recoverable(error)) {
                result[key] = null;
                errors.Add(new JObject { ["field"] = key, ["reason"] = (error as GraphQL.ExecutionError)?.Code ?? error.GetType().Name, ["message"] = error.Message });
            }
        }
        if (condition is GameCondition_PsychicEmanation psychic) {
            result["psychic_gender"] = psychic.gender.ToString(); result["psychic_level"] = psychic.level.ToString();
        }
        if (condition is GameCondition_PsychicSuppression suppression) result["psychic_gender"] = suppression.gender.ToString();
        if (condition is GameCondition_HateChantDrone chant) result["hate_chant_level"] = chant.level.ToString();
        result["presentation"] = new JObject { ["status"] = errors.Count == 0 ? "ok" : "partial",
            ["source"] = "native condition instance Label/Description getters", ["native_getters"] = sources, ["errors"] = errors };
        return result;
    }
}
