using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal interface IQueryData
{
    IEnumerable<GameNode> QueryThings(IReadOnlyDictionary<string, object?> arguments, ReadContext context, Func<GameNode,bool>? definitionFilter = null, IReadOnlyCollection<string>? exactIds = null);
    IEnumerable<GameEntity> QueryPawns(IReadOnlyDictionary<string, object?> arguments, ReadContext context);
}

/// <summary>A compiled selection over one public type: fields, predicate, measures, grouping, order and page.</summary>
internal sealed class PublicQuerySelection
{
    public string Type = "";
    public List<(string Name, FieldType Field, Dictionary<string, object?> Args, PublicQuerySelection? Child)> Fields = new();
    public Dictionary<string, PublicQuerySelection> On = new();
    public SelectionConditions.Predicate? Predicate;
    public JToken Where = new JObject();
    public int Limit = 20;
    public int Offset;
    public List<(string Name, string Op, string? Path)> Measures = new();
    public string[] GroupBy = Array.Empty<string>();
    public List<(string Path, bool Desc)> Order = new();
}

/// <summary>The select input a selection is compiled from, before or after syntax parsing.</summary>
internal sealed class PublicQueryProjection
{
    public JToken? Token;
    public string Path = "";
    public bool Generated;
    public IReadOnlyList<SelectionInput.Field>? Fields;
}

/// <summary>One validated batch request: its root read, arguments, selection and pushdown.</summary>
internal sealed class PublicQueryRequest
{
    public PublicQueryRequest(string root, FieldType rootField, Dictionary<string, object?> args, PublicQuerySelection select,
                              SelectionConditions.Predicate? definitionFilter, bool fullyPushed, IReadOnlyCollection<string>? exactIds)
    {
        Root = root;
        RootField = rootField;
        Args = args;
        Select = select;
        DefinitionFilter = definitionFilter;
        FullyPushed = fullyPushed;
        ExactIds = exactIds;
    }
    public string Root { get; }
    public FieldType RootField { get; }
    public Dictionary<string, object?> Args { get; }
    public PublicQuerySelection Select { get; }
    public SelectionConditions.Predicate? DefinitionFilter { get; }
    public bool FullyPushed { get; }
    public IReadOnlyCollection<string>? ExactIds { get; }
}

/// <summary>Per-execution state: read context, issues, object budget, phases and cached path values.</summary>
internal sealed class PublicQueryRun
{
    public readonly ReadContext Context;
    public readonly List<QueryIssue> Issues = new();
    public int Objects;
    public readonly JArray Phases = new();
    public readonly Dictionary<string,string> RelationSemantics = new();
    public readonly Dictionary<(object Value,string Type,string Path),JToken> PathValues = new();
    public readonly Dictionary<string,List<(string Type,FieldType Field,Dictionary<string,object?> Args)>> PathSteps = new();
    public PublicQueryRun(ReadContext context) { Context = context; }
    public void Check() { Context.Check(); if (++Objects > 20000) throw ReadContext.Error("query_limit","Query exceeds 20000 projected objects; narrow fields or rows."); }
    public void Fail(ExecutionError error,string path) => Issues.Add(new QueryIssue { Code = error.Code ?? "field_unavailable", Message = error.Message, Path = new JArray(path.Split('/').Where(s => s.Length > 0)) });
}
