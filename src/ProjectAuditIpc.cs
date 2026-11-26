using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolsMcp;

internal static partial class EncyIpc
{
    private static List<HoleReading> ReadHoles(Handle op, Handle project, AssemblyLoadContext runtime, double scale)
    {
        var job = op.Object("get_ModelFormerJobAssignment");
        var holeType = runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.ModelFormerTypes"))
            .GetType("CAMIPC.ModelFormerTypes.ICamIpcHoleModelItem", true)!;
        var holes = new List<HoleReading>();
        for (int i = 0, count = job.Count("get_Count"); i < count; i++)
        {
            var item = job.Object("get_Item", i);
            if (!holeType.IsInstanceOfType(item.Value)) {
                holes.Add(new(null, null, null, true));
                continue;
            }
            var hole = new Handle(item.Value, holeType);
            double d = Convert.ToDouble(hole.Call("get_Diameter", []).Value) * scale;
            double depth = (Convert.ToDouble(hole.Call("get_TopLevel", []).Value) -
                Convert.ToDouble(hole.Call("get_BottomLevel", []).Value)) * scale;
            bool levelsKnown = hole.Call("get_TopLevelMode", []).Value?.ToString() != "htlmDefault" &&
                hole.Call("get_BottomLevelMode", []).Value?.ToString() != "hblmDefault";
            var shape = FeatureGeometry(project, runtime, item.Text("get_Caption"), d, scale, false);
            string? bottom = shape?.Kind;
            if (bottom == "blind") bottom = hole.Call("get_DrillTipCompensation", []).Value?.ToString() switch {
                "hdtNone" => "tip", "hdtToolTip" => "cylinder", _ => "blind"
            };
            holes.Add(new(PositiveMm(d), levelsKnown ? PositiveMm(depth) : null, bottom));
        }
        return holes;
    }

    private static List<Shape?> ReadMillShapes(Handle op, Handle project, AssemblyLoadContext runtime, double scale)
    {
        var job = op.Object("get_ModelFormerJobAssignment");
        var features = new List<Shape?>();
        for (int i = 0, count = job.Count("get_Count"); i < count; i++)
            features.Add(FeatureGeometry(project,runtime,job.Object("get_Item",i).Text("get_Caption"),0,scale,true));
        return features;
    }

    private static double? PositiveMm(double value) => double.IsFinite(value) && value > 0 ? value : null;

    private sealed record OperationSnapshot(string Id, string Name, string Type, string? Machine, string? Setup,
        string? Material, string? NodeId, string? LegacyId, List<HoleReading>? Holes, OperationBrief? Mill, string? Error);

    private static bool ReadError(Exception e) =>
        e is TargetInvocationException or COMException or NotSupportedException or InvalidOperationException;

    private static T? Optional<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception e) when (ReadError(e)) { return null; }
    }

    private static OperationSnapshot Snapshot(Handle op, Handle project, AssemblyLoadContext runtime, double scale,
        string? setup, Type? assemblyType)
    {
        string id = op.Text("GetId"), name = op.Text("GetName");
        var xml = op.Object("get_XMLProp");
        string type = xml.Text("get_Name");
        string? machine = Optional(() => op.Object("get_Machine").Text("get_MachineCaption"));
        string? nodeId = assemblyType?.IsInstanceOfType(op.Value) == true ? Optional(() => {
            int n = new Handle(op.Value, assemblyType).Count("get_ToolNodeID");
            return n < 0 ? null : n.ToString(CultureInfo.InvariantCulture);
        }) : null;
        string? legacyId = Optional(() => {
            var array = xml.Object("GetArray", "ToolSection.Tools");
            return array.Count("get_TopItem") < 0 ? null : array.Object("get_Itm",0).Text("GetCalculatedString","ToolID");
        });
        const string mat = "ConditionsSection.Workpiece.Material";
        string? material = Optional(() => xml.Count("get_PropExists", mat) != 0 ? xml.Text("GetString", mat) : null);
        var snapshot = new OperationSnapshot(id,name,type,machine,setup,material,nodeId,legacyId,null,null,null);
        try {
            if (SuitableCycle(xml,true)) {
                var context = new JsonObject { ["features"] = JsonSerializer.SerializeToNode(ReadMillShapes(op,project,runtime,scale)) };
                return snapshot with { Mill = OperationBrief.FromContext(context,null,null,null,0,null,null,material ?? "",null,0,20) };
            }
            return SuitableCycle(xml,false) ? snapshot with { Holes = ReadHoles(op,project,runtime,scale) } : snapshot;
        } catch (Exception e) when (ReadError(e)) {
            return snapshot with { Error = e.GetBaseException().Message };
        }
    }

    private static int AuditProject(Handle project, AssemblyLoadContext runtime, string path, int pid, string units)
    {
        if (units is not ("metric" or "imperial")) throw new ArgumentException("Specify metric or imperial.");
        double scale = units == "metric" ? 1 : 25.4;
        var tech = project.Object("GetTechnologist");
        var enumType = tech.Type.GetMethod("GetOperations")!.GetParameters()[0].ParameterType;
        var iter = tech.Object("GetOperations", Enum.ToObject(enumType, 0));
        Type? assemblyType = null;
        if (project.Type.GetMethod("get_MachineTools") != null)
            assemblyType = runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.ToolAssemblies"))
                .GetType("CAMIPC.ToolAssemblies.ICamIpcTechOperationWithToolAssembly");

        var operations = new List<OperationSnapshot>();
        void VisitChildren(string? setup) {
            if (iter.Count("MoveToChild") == 0) return;
            do {
                var op = iter.Object("Current");
                var s = Snapshot(op, project, runtime, scale, setup, assemblyType);
                bool group = s.Type.EndsWith("Group",StringComparison.OrdinalIgnoreCase);
                if (!group) operations.Add(s);
                VisitChildren(s.Type == "TSTStageOpGroup" ? s.Name : setup);
            } while (iter.Count("MoveToSibling") != 0);
            if (iter.Count("MoveToParent") == 0) throw new InvalidOperationException("Operation tree changed during inspection.");
        }
        VisitChildren(null);

        var drillingDiameters = operations.Where(s => s.Holes != null).SelectMany(s => s.Holes!)
            .Where(h => h.Bottom is "through" or "cylinder" && h.DiameterMm.HasValue).Select(h => h.DiameterMm!.Value).Distinct().ToArray();
        bool NeedsDrillAngle(double d) => drillingDiameters.Any(required => Math.Abs(d-required) <= 1e-7);
        var inventory = Inventory(project,scale,NeedsDrillAngle,includeIncomplete:true);
        AuditCutter Describe(Installed t) => new(t.ID,t.Caption,t.Type,PositiveMm(t.DiameterMm),
            PositiveMm(t.ReachMm),InstalledDrillPoint.ValidAngle(t.Angle) ? t.Angle : null,
            t.Magazine,t.Position,t.Node ? "machineMagazine" : "projectToolList",t.Adapters,
            !InstalledDrillPoint.IsStandardDrill(t.Type) ? null : InstalledDrillPoint.ValidAngle(t.Angle) ? "known" :
                t.Node && !NeedsDrillAngle(t.DiameterMm) ? "notRead" : "unavailable");
        var rows = new List<ProjectAuditRow>();
        foreach (var s in operations) {
            // Node IDs and legacy tool IDs occupy different namespaces; never match by T position.
            var tool = s.NodeId != null ? inventory.FirstOrDefault(t => t.Node && t.ID == s.NodeId)
                : inventory.FirstOrDefault(t => !t.Node && t.ID == s.LegacyId);
            var cutter = tool == null ? null : Describe(tool);
            GeometryReview review;
            if (s.Error != null) review = new("unavailable", ["operationGeometry"], [new("geometryReadFailed")]);
            else if (s.Holes != null) review = ProjectToolAudit.Drill(s.Holes,cutter);
            else if (s.Mill != null) review = ProjectToolAudit.Mill(s.Mill,cutter);
            else review = new("unsupportedOperation", [], []);
            string? assignedId = s.NodeId ?? s.LegacyId;
            if (cutter == null && !string.IsNullOrWhiteSpace(assignedId) && review.Missing.Remove("assignedTool")) {
                review.Missing.Add("assignedToolGeometry");
                review.Reasons.Add(new("assignedIdNotInInventory"));
            }
            rows.Add(new(s.Id,s.Name,s.Type,s.Material,assignedId,cutter,review,s.Mill,s.Machine,s.Setup,s.Error));
        }
        var searchable = rows.Where(r => r.Review.Status is "fitsGeometry" or "needsInput" or "mismatch").ToList();
        var noMaterial = searchable.Where(r => string.IsNullOrWhiteSpace(r.Material)).Select(r=>r.OperationId).ToArray();
        var catalogueQuestions = new List<AuditQuestion>();
        if (noMaterial.Length > 0) catalogueQuestions.Add(new("workpieceMaterialForCuttingData",noMaterial));
        if (searchable.Count > 0) catalogueQuestions.Add(new("spindleInterfaceForHolderCheck",searchable.Select(r=>r.OperationId).ToArray()));
        var report = new AuditReport(pid,path,units,0,rows,inventory.Select(Describe).ToList(),
            ProjectToolAudit.Questions(rows),catalogueQuestions.ToArray());
        Console.WriteLine(JsonSerializer.Serialize(report, AuditOutput.Json));
        return 0;
    }
}
