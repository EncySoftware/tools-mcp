using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace ToolsMcp;

internal static partial class EncyIpc
{
    private sealed record Shape(string Kind,double Height,double? Corner = null,double? BottomRadius = null);
    private static bool SuitableCycle(Handle xml,bool mill) => mill
        ? xml.Text("get_Name") is "TST2DContouringOp" or "TST25DContouringOp" or "TST25DPocketingOp"
             or "TSTPocketingOp" or "TSTRoughingWaterlineOp" or "TSTPlaneWaterlineOp"
        : xml.Count("get_PropExists","DrillingType")!=0 && xml.Count("GetInteger","DrillingType") is >=0 and <=3;

    private static Shape? FeatureGeometry(Handle project, AssemblyLoadContext runtime, string path, double diameter, double scale, bool mill, List<RecognizedHole>? recognized = null)
    {
        if(!path.Contains('\\'))return null;
        try {
            var finder=project.Object("get_FeatureFinder");
            var matrix=finder.Type.GetMethod("GetFeaturesForNode")!.GetParameters()[2].ParameterType;
            if(matrix.IsByRef)matrix=matrix.GetElementType()!;
            var list=finder.Object("GetFeaturesForNode",path,false,Activator.CreateInstance(matrix));
            if (!mill && recognized != null) {
                try { recognized.AddRange(RecognizedHoles(list,runtime,path,diameter,scale)); }
                catch (Exception e) when (ReadError(e) || e is InvalidDataException) { /* Legacy recognition remains available. */ }
            }
            var type=runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.FeatureFinder"))
                .GetType("CAMIPC.FeatureFinder."+(mill?"ICamIpcPocketFeature":"ICamIpcHoleFeature"),true)!;
            var found=new HashSet<Shape>();
            void Visit(Handle feature,int level) {
                if(level>6 || feature.Count("get_IsValid")==0)return;
                if(type.IsInstanceOfType(feature.Value)){
                    var shape=new Handle(feature.Value,type);
                    double Number(string key)=>Convert.ToDouble(shape.Call("get_"+key,[]).Value);
                    double height=Number("Height")*scale;
                    if(mill && height>0) {
                        double radius=Number("MinCornerRadius")*scale;
                        string kind=shape.Call("get_PocketType",[]).Value?.ToString() switch {
                            "captPocket"=>"pocket", "captSide"=>"shoulder", "captBoss"=>"boss", _=>"unknown"};
                        double? floor=null;
                        if(kind=="pocket" && shape.Type.GetMethod("get_BottomRadius")!=null && shape.Type.GetMethod("get_IsThroughFeature")!=null){
                            try {
                                if(shape.Count("get_IsThroughFeature")!=0) kind="throughPocket";
                                else {
                                    var raw=shape.Call("get_BottomRadius",[]).Value;
                                    if(raw is double value && double.IsFinite(value) && value>=0) floor=value*scale;
                                }
                            } catch(Exception e) when(ReadError(e)){ /* optional on older ENCY versions */ }
                        }
                        found.Add(new Shape(kind,height,radius>0?radius:null,floor));
                    } else if(!mill && height>0 && Math.Abs(Number("Diameter")*scale-diameter)<1e-5)
                        found.Add(new Shape(shape.Count("get_IsBlind")!=0?"blind":"through",height));
                }
                for(int i=0;i<feature.Count("get_SubFeatureCount");i++)Visit(feature.Object("get_SubFeature",i),level+1);
            }
            for(int i=0;i<list.Count("get_Count");i++)Visit(list.Object("get_Feature",i),0);
            return found.Count==1?found.Single():null;
        } catch(Exception){return null;}
    }

    private sealed record Installed(string ID,string Identity,string Caption,string Type,double DiameterMm,double ReachMm,
        double? Angle,string Magazine,int Position,bool Node, IReadOnlyList<string>? Adapters = null, bool ReachVerified = true,
        string? MagazineCaption = null);
    private static List<Installed> Inventory(Handle project,double scale, Func<double, bool> needsAngle, bool includeIncomplete = false) {
        var result=new List<Installed>();
        Handle? machine=null;
        if(project.Type.GetMethod("get_MachineTools")!=null){
            var obj=project.Call("get_MachineTools",[]);
            if(obj.Value!=null)machine=new Handle(obj.Value,obj.Type);
        }
        if(machine!=null && machine.Count("GetMagazinesCount")>0){
            for(int i=0;i<machine.Count("GetMagazinesCount");i++){
                var mag=machine.Object("GetMagazine",i);
                for(int j=0;j<mag.Count("GetToolsCount");j++){
                    var node=mag.Object("GetTool",j);
                    var component=node.Object("GetComponent");
                    var toolType = component.Text("GetToolType");
                    double diameter = Convert.ToDouble(component.Call("GetDiameter",[]).Value);
                    double? angle = null;
                    if (needsAngle(diameter) && InstalledDrillPoint.IsStandardDrill(toolType) && component.Type.GetMethod("GetParameters") != null &&
                        component.Type.GetMethod("GetParameter") != null) {
                        try {
                            angle = InstalledDrillPoint.ReadAngle(component.Strings("GetParameters"),
                                key => (double)component.Call("GetParameter", [key]).Value!);
                        } catch (Exception e) when (e is InvalidOperationException or TargetInvocationException or System.Runtime.InteropServices.COMException) {
                            // Optional geometry: the tool remains available for tip-referenced searches.
                        }
                    }
                    double working = Convert.ToDouble(component.Call("GetWorkingLength",[]).Value);
                    // a kit that gave no usable length leaves ENCY's template value, half the overall length: not the drill's reach
                    bool reachVerified = true;
                    if (InstalledDrillPoint.IsStandardDrill(toolType) && component.Type.GetMethod("GetParameters") != null &&
                        component.Type.GetMethod("GetParameter") != null) {
                        try {
                            string? lengthKey = component.Strings("GetParameters").FirstOrDefault(k => k.Equals("Length", StringComparison.OrdinalIgnoreCase));
                            if (lengthKey != null)
                                reachVerified = !InstalledDrillPoint.TemplateReach(working, Convert.ToDouble(component.Call("GetParameter", [lengthKey]).Value));
                        } catch (Exception e) when (e is InvalidOperationException or TargetInvocationException or System.Runtime.InteropServices.COMException) {
                            // the reach stays as ENCY reports it
                        }
                    }
                    result.Add(new Installed(node.Count("get_NodeID").ToString(CultureInfo.InvariantCulture),
                        Convert.ToString(component.Call("get_ID",[]).Value,CultureInfo.InvariantCulture)??"",node.Text("GetCaption"),toolType,
                        diameter,working,
                        angle,mag.Text("GetName"),node.Count("GetPosition"),true,
                        includeIncomplete ? Optional(() => node.Strings("GetAdapters")) : null, reachVerified,
                        // the magazine as ENCY's window names it, which is what a technologist writes as a place
                        Optional(() => mag.Text("GetCaption"))));
                }
            }
            if (!includeIncomplete) return result;
        }
        var tools=project.Object("get_ToolsList");
        for(int i=0;i<tools.Count("get_Count");i++){
            var tool=tools.Object("get_ToolInfo",i);var props=tool.Object("get_ToolProperties");
            double Read(string k)=>props.Count("get_PropExists","Properties."+k)==0?0:Convert.ToDouble(props.Call("GetFloat",["Properties."+k]).Value);
            var limits=new[]{Read("WorkingLength"),Read("ShoulderLength")}.Where(n=>n>0&&double.IsFinite(n)).ToArray();
            double length = Read("Length");
            double reach = limits.Length > 0 && double.IsFinite(length) && length > 0 ? Math.Min(length, limits.Min())*scale : 0;
            if (!includeIncomplete && reach <= 0) continue;
            result.Add(new Installed(tool.Text("get_ToolID"),tool.Text("get_ToolGUID"),tool.Text("get_ToolCaption"),tool.Text("get_ToolType"),
                Read("Diameter")*scale,reach,Read("Angle"),includeIncomplete ? tool.Count("get_MagazineNumber").ToString(CultureInfo.InvariantCulture) : tool.Text("get_ConnectorID"),tool.Count("get_ToolNumber"),false));
        }
        return result;
    }

    private static int MachineCommand(string[] args,Handle project,AssemblyLoadContext runtime,string path){
        var tech=project.Object("GetTechnologist");var op=tech.Object("get_CurrentOperation");var xml=op.Object("get_XMLProp");
        var id=op.Text("GetId");
        if(args[0]=="mill-context" && !SuitableCycle(xml,true))
            throw new InvalidOperationException("Select a contouring, pocketing or roughing milling operation.");
        string units=args[0]=="mill-context" ? ResolveOperationUnits(tech,op,args[2]) : args[2];
        if(units is not ("metric" or "imperial"))throw new ArgumentException("Specify metric or imperial.");
        double scale=units=="imperial"?25.4:1;
        if(args[0]=="mill-context"){
            var geometry = ReadMillGeometry(op, project, runtime, scale);
            const string key="ConditionsSection.Workpiece.Material";
            Console.WriteLine(JsonSerializer.Serialize(new{processId=int.Parse(args[1],CultureInfo.InvariantCulture),projectPath=path,operationId=id,operationName=op.Text("GetName"),
                material=xml.Count("get_PropExists",key)==0?null:xml.Text("GetString",key),
                operationUnits=units,unitsSource=args[2]=="auto"?"operationExport":"caller",
                features=geometry.Features,availableWidthMm=geometry.AvailableWidthMm,widthSource=geometry.WidthSource,
                bottomRadiusMm=geometry.BottomRadiusMm,bottomRadiusSource=geometry.BottomRadiusMm.HasValue?"recognizedPocketFeature":null}));
            return 0;
        }
        string mode=args[3];
        bool mill=mode is "slot" or "shoulder";
        if(!SuitableCycle(xml,mill))throw new InvalidOperationException("Selected operation does not match the search.");
        double Parse(string s)=>double.Parse(s,CultureInfo.InvariantCulture);
        double width=Parse(args[4]),depth=Parse(args[5]),extra=Parse(args[6]);double? radius=args[7].Length==0?null:Parse(args[7]);
        if(mode is not ("slot" or "shoulder" or "tip" or "cylinder" or "through") || !double.IsFinite(width)||width<=0
            || !double.IsFinite(depth)||depth<=0||!double.IsFinite(extra)||extra<0||(mode!="through"&&extra!=0)
            ||(radius is double r && (!double.IsFinite(r)||r<0)))throw new ArgumentException("Invalid search dimensions.");
        double Required(Installed t) => mill || mode == "tip" ? depth :
            InstalledDrillPoint.RequiredDepth(t.DiameterMm, depth, extra, t.Angle) ?? double.PositiveInfinity;
        var inventory = Inventory(project, scale, d => mode is ("through" or "cylinder") && Math.Abs(d-width) <= 1e-7);
        var results=inventory.Where(t=>{
            var shape=t.Type.Split('.').Last();
            if(!(t.DiameterMm>0 && t.ReachMm>0))return false;
            if(mill)return shape.Equals("EndMill",StringComparison.OrdinalIgnoreCase) && t.DiameterMm<=width && (radius==null||t.DiameterMm<=2*radius)&&t.ReachMm>=depth;
            if(!InstalledDrillPoint.IsStandardDrill(t.Type)||Math.Abs(t.DiameterMm-width)>1e-7||!t.ReachVerified)return false;
            return t.ReachMm >= Required(t);
        }).ToList();
        if(args[0]=="machine-fit"){
            var installed = results.Select(t => new {
                t.ID, t.Identity, t.Caption, t.Type, t.DiameterMm, t.ReachMm, t.Angle, t.Magazine, t.Position, t.Node,
                requiredMm = Required(t), source = t.Node ? "machineMagazine" : "projectToolList"
            });
            // In a legacy project the assigned tool ID belongs to the project list, not to the magazine-node namespace.
            var active = xml.Object("GetArray", "ToolSection.Tools");
            var assignedId = active.Count("get_TopItem") < 0 ? null :
                active.Object("get_Itm", 0).Text("GetCalculatedString", "ToolID");
            var current = inventory.FirstOrDefault(t => !t.Node && t.ID == assignedId);
            var assignedTool = current == null ? null : new {
                current.ID, current.Caption, current.Position, current.DiameterMm, current.ReachMm,
                matchesGeometry = results.Contains(current)
            };
            var unverified = inventory.Where(t => mode is ("through" or "cylinder") &&
                InstalledDrillPoint.IsStandardDrill(t.Type) && Math.Abs(t.DiameterMm-width) <= 1e-7 && t.ReachMm >= depth + extra &&
                t.ReachVerified && !InstalledDrillPoint.ValidAngle(t.Angle)).Select(t => new {
                    t.ID, t.Identity, t.Caption, t.Position, t.Magazine, t.DiameterMm, t.ReachMm,
                    reasonCode = "missingPointAngle", reason = "The installed drill's point angle is unknown."
                }).Concat(inventory.Where(t => !mill && !t.ReachVerified && InstalledDrillPoint.IsStandardDrill(t.Type) &&
                    Math.Abs(t.DiameterMm-width) <= 1e-7).Select(t => new {
                    t.ID, t.Identity, t.Caption, t.Position, t.Magazine, t.DiameterMm, t.ReachMm,
                    reasonCode = "templateWorkingLength",
                    reason = "The installed drill's working length is ENCY's template value (half the overall length), not its usable length."
                }));
            Console.WriteLine(JsonSerializer.Serialize(new{projectPath=path,operationId=id,installed,assignedTool,unverified}));
            return 0;
        }
        if(string.IsNullOrWhiteSpace(path)||!string.Equals(path,args[8],StringComparison.OrdinalIgnoreCase)||!string.Equals(id,args[9],StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selected project or operation changed.");
        var choice=results.SingleOrDefault(t=>t.ID==args[10] && !string.IsNullOrWhiteSpace(t.Identity) && t.Identity==args[11])
            ?? throw new InvalidOperationException("Installed tool or geometry changed.");
        SetInstalled(project, runtime, op, id, choice.ID, choice.Node);
        Console.WriteLine($"{op.Text("GetName")} [{id}] → {choice.Magazine} T#{choice.Position} {choice.Caption}");
        return 0;
    }

    private static void SetInstalled(Handle project,AssemblyLoadContext runtime,Handle op,string id,string toolId,bool node)
    {
        if (node) {
            var type=runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.ToolAssemblies")).GetType("CAMIPC.ToolAssemblies.ICamIpcTechOperationWithToolAssembly",true)!;
            var withTool=new Handle(op.Value,type);
            int n=int.Parse(toolId,CultureInfo.InvariantCulture);
            withTool.Call("SetTool",[n]);
            if(withTool.Count("get_ToolNodeID")!=n) throw new InvalidOperationException("Magazine assignment not confirmed.");
        } else {
            project.Call("SetOperationTool",[id,toolId]);
            var active=op.Object("get_XMLProp").Object("GetArray","ToolSection.Tools").Object("get_Itm",0);
            if(active.Text("GetCalculatedString","ToolID")!=toolId) throw new InvalidOperationException("Project tool assignment not confirmed.");
        }
    }
}
