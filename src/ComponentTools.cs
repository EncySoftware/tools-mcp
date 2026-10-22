using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>One component: its parameters, what fits it, the chain up to the spindle, and its files.</summary>
[McpServerToolType]
public class ComponentTools(IToolsApi api, TokenProvider tokens, IFileSink? files = null)
{
    /** Where a saved file goes: the person's disk at home, a link on the hosted server. */
    private readonly IFileSink _files = files ?? LocalFiles.Instance;

    /** The names of check_fit's ids are looked up one by one, so the ids are capped; the API's own cap is 200. */
    internal const int MaxFitIds = 50;

    [McpServerTool(Name = "describe_component"), Description(
        "One component by its id: what it is, every parameter its supplier publishes, its connection with how many " +
        "parts it goes into and takes, and which files (model, drawing, photo) exist for it.")]
    public Task<string> DescribeComponent([Description("The component's id, from find_components")] string id) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } cid) return Ids.NotAComponent(id);
            var detail = await api.GetComponent(cid, token);
            if (detail == null) return NoComponent(cid);
            var compat = await api.GetCompat(cid, token);
            var c = detail.Component;
            var reply = new StringBuilder(Replies.Line(c)).AppendLine();
            if (c.Kind != null) reply.AppendLine("Kind: " + c.Kind);
            if (detail.Params is { Count: > 0 } p)
            {
                reply.AppendLine("Parameters:");
                foreach (var kv in p.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    reply.AppendLine($"  {kv.Key}: {Text(kv.Value)}");
            }
            if (compat != null)
                reply.AppendLine($"Connection: {compat.PlugLabel ?? compat.PlugKey ?? "none"}; goes into {compat.PlugsIntoTotal}, "
                                 + $"takes {compat.AcceptsTotal}");
            var files = new List<string>();
            if (c.Model) files.Add("model");
            if (c.Drawing) files.Add("drawing");
            if (c.Picture) files.Add("picture");
            reply.Append(files.Count > 0
                ? "Files: " + string.Join(", ", files) + $" - save_component_file(\"{c.Id}\", kind) saves one."
                : "No model, drawing or photo on tools.encycam.com.");
            return reply.ToString();
        });

    [McpServerTool(Name = "what_fits"), Description(
        "What a component goes into (holders, adaptors) and what it takes (inserts, tools), with the totals. For the " +
        "whole list page through find_components(fitsComponentId).")]
    public Task<string> WhatFits(
        [Description("The component's id")] string id,
        [Description("both, holders (what it goes into) or accepts (what it takes)")] string direction = "both",
        [Description("Rows of each side to show, 1 to 100")] int limit = 20) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } cid) return Ids.NotAComponent(id);
            if (direction is not ("both" or "holders" or "accepts")) return "ERROR: direction is both, holders or accepts";
            var compat = await api.GetCompat(cid, token);
            if (compat == null) return NoComponent(cid);
            int shown = Math.Clamp(limit, 1, 100);
            var reply = new StringBuilder();
            if (compat.PlugLabel != null) reply.AppendLine("Connection: " + compat.PlugLabel);
            if (direction is "both" or "holders") Side("Goes into", compat.PlugsIntoTotal, compat.PlugsInto, shown, reply);
            if (direction is "both" or "accepts") Side("Takes", compat.AcceptsTotal, compat.Accepts, shown, reply);
            reply.Append($"All of them: find_components(fitsComponentId: \"{cid}\")");
            return reply.ToString();
        });

    [McpServerTool(Name = "assemble_to_spindle"), Description(
        "The chain above a component up to the part that mounts in the machine: what holds it, what holds that, " +
        "with how many other choices each step had - or why the chain stopped. Name the machine's spindle and the " +
        "chain is the shortest one that ends on it; where none does, the reply names the spindles the chains reach.")]
    public Task<string> AssembleToSpindle(
        [Description("The component's id, e.g. a cutter or an insert")] string id,
        [Description("The machine's spindle as ENCY names it - HSK63, SK40, ISO40, BT40, NT40, Capto C6, KM63; " +
                     "leave out for any")] string? spindle = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } cid) return Ids.NotAComponent(id);
            var chain = await api.GetAssembly(cid, string.IsNullOrWhiteSpace(spindle) ? null : spindle.Trim(), token);
            if (chain == null) return NoComponent(cid);
            var reply = new StringBuilder();
            for (int i = 0; i < chain.Steps.Count; i++)
            {
                var step = chain.Steps[i];
                reply.AppendLine($"{i + 1}. {Replies.Line(step.Component)} (+{step.Alternatives} other choices)");
            }
            if (chain.Steps.Count == 0) reply.AppendLine("Nothing in the catalogue holds it.");
            // which spindle, where the site says: "mounts in a HSK63 spindle"
            reply.Append(chain.ReachesSpindle
                ? "Reaches the spindle" + (chain.StoppedBecause is { } on && on.StartsWith("mounts in a ") ? ": " + on : "") + "."
                : "Stopped: " + chain.StoppedBecause);
            return reply.ToString();
        });

    [McpServerTool(Name = "check_fit"), Description(
        "Checks a hand-picked set of components before building it: which of them takes which. 2 to 50 ids.")]
    public Task<string> CheckFit([Description("The components' ids")] string[] componentIds) =>
        Replies.WithToken(tokens, async token =>
        {
            if (componentIds.Length is < 2 or > MaxFitIds) return $"ERROR: check_fit takes 2 to {MaxFitIds} component ids";
            var (ids, bad) = Ids.All(componentIds);
            if (bad != null) return Ids.NotAComponent(bad);
            var names = await NamesOf(ids, token);
            var takes = await api.GetAcceptsAmong(ids, token);
            var reply = new StringBuilder();
            var fitting = new HashSet<string>();
            foreach (var (holder, taken) in takes)
                foreach (var part in taken)
                {
                    reply.AppendLine($"{names.GetValueOrDefault(holder, holder)} takes {names.GetValueOrDefault(part, part)}");
                    fitting.Add(holder);
                    fitting.Add(part);
                }
            var alone = ids.Where(i => !fitting.Contains(i)).Distinct().ToList();
            if (fitting.Count == 0) reply.AppendLine("None of these fits another.");
            if (alone.Count > 0)
                reply.Append("Fit with none of the others: " + string.Join(", ", alone.Select(i => $"{names[i]} [id {i}]")));
            return reply.ToString().TrimEnd();
        });

    [McpServerTool(Name = "save_component_file"), Description(
        "Saves a component's supplier STEP model, dimensioned drawing or photo to the person's disk, named after the " +
        "component, and says where.")]
    public Task<string> SaveComponentFile(
        [Description("The component's id")] string id,
        [Description("model, drawing or picture")] string kind = "model",
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            _files.Check(folder);
            if (Ids.Of(id) is not { } cid) return Ids.NotAComponent(id);
            ComponentFileKind? fileKind = kind switch
            {
                "model" => ComponentFileKind.Model,
                "drawing" => ComponentFileKind.Drawing,
                "picture" => ComponentFileKind.Picture,
                _ => null,
            };
            if (fileKind == null) return "ERROR: kind is model, drawing or picture";
            var detail = await api.GetComponent(cid, token);
            if (detail == null) return NoComponent(cid);
            string name = detail.Component.Name;
            var file = await api.GetComponentFile(cid, fileKind.Value, token);
            if (file == null) return $"ERROR: {name} has no {kind} on tools.encycam.com";
            string fileName = fileKind switch
            {
                ComponentFileKind.Model => name + ".stp",
                ComponentFileKind.Drawing => name + " - drawing.jpg",
                _ => name + " - photo.jpg",
            };
            return _files.Deliver(file with { FileName = fileName }, folder);
        });

    private static string NoComponent(string id) =>
        $"ERROR: no component {id} - the id is wrong or it is in a catalogue this account cannot see";

    private static void Side(string title, long total, IReadOnlyList<Component> parts, int shown, StringBuilder reply)
    {
        int listed = Math.Min(shown, parts.Count);
        reply.AppendLine($"{title} {total}" + (total > listed ? $" (first {listed}):" : ":"));
        foreach (var c in parts.Take(listed)) reply.AppendLine("  " + Replies.Line(c));
    }

    /** A JSON value as a person reads it: a string without its quotes, anything else as JSON. */
    private static string Text(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";

    /** The names of the ids, eight lookups at a time; an id that names nothing keeps its id. */
    private async Task<Dictionary<string, string>> NamesOf(IReadOnlyList<string> ids, string token)
    {
        using var gate = new SemaphoreSlim(8);
        var lookups = ids.Distinct().Select(async id =>
        {
            await gate.WaitAsync();
            try { return (id, name: (await api.GetComponent(id, token))?.Component.Name ?? id + " (not found)"); }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(lookups)).ToDictionary(x => x.id, x => x.name);
    }
}
