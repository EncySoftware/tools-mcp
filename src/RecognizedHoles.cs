using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;

namespace ToolsMcp;

internal sealed record HoleChamfer(double SizeMm,double? AngleDegrees);
internal sealed record HoleThread(double OuterDiameterMm,double? HeightMm,double? ZMinMm,double? ZMaxMm);
internal sealed record HoleFrame(GeometryPoint OriginMm,GeometryPoint Axis);
internal sealed record HoleStage(string FeatureId,double DiameterMm,double DepthMm,double? ZMinMm,double? ZMaxMm,
    bool IsBlind,bool? IsCrossHole,double? TipAngleDegrees,HoleChamfer? EntranceChamfer,HoleThread? Thread,HoleFrame? FeatureLcs);
internal sealed record RecognizedHole(string AssignedNode,string FeatureId,string Kind,List<HoleStage> Steps,bool AllChildStepsRead);

internal static partial class EncyIpc
{
    private static int SelectedHoleFeatures(Handle project,AssemblyLoadContext runtime,int pid,string path,string requested) {
        string units=requested;
        if(requested=="auto"){
            var tech=project.Object("GetTechnologist");
            var active=tech.Call("get_CurrentOperation",[]);
            if(active.Value==null)throw new ArgumentException("Specify the displayed metric or imperial units for this project without an active operation.");
            var op=new Handle(active.Value,active.Type);
            if(op.Object("get_XMLProp").Text("get_Name").EndsWith("Group",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Specify metric or imperial to inspect geometry before any machining operation exists.");
            units=ResolveOperationUnits(tech,op,"auto");
        }
        if(units is not ("metric" or "imperial"))throw new ArgumentException("Specify auto, metric or imperial.");
        var finder=project.Object("get_FeatureFinder");
        double scale=units=="imperial"?25.4:1;
        var selected=finder.Object("GetSelectedFeatures");
        var geometry=finder.Object("GetFeaturesForSelected");
        var features=RecognizedHoles(selected,runtime,"selected feature",double.NaN,scale)
            .Concat(RecognizedHoles(geometry,runtime,"selected model geometry",double.NaN,scale))
            .DistinctBy(h=>h.FeatureId).ToList();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { processId=pid,projectPath=path,operationUnits=units,
            unitsSource=requested=="auto"?"operationExport":"caller",features },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return 0;
    }

    private static List<RecognizedHole> RecognizedHoles(Handle list, AssemblyLoadContext runtime,string path,double diameter,double scale)
    {
        var result=new List<RecognizedHole>();
        var assembly=runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.FeatureFinder"));
        var holeType=assembly.GetType("CAMIPC.FeatureFinder.ICamIpcHoleFeature",true)!;
        var compoundChildren=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var simple=new List<HoleStage>();
        int budget=1024;
        HoleStage? Stage(Handle feature) {
            if (!holeType.IsInstanceOfType(feature.Value))return null;
            var hole=new Handle(feature.Value,holeType);
            double? Value(Handle h,string key,double factor=1){
                if(h.Type.GetMethod("get_"+key)==null)return null;
                try{var raw=h.Call("get_"+key,[]).Value;if(raw==null)return null;
                    double value=Convert.ToDouble(raw,CultureInfo.InvariantCulture)*factor;
                    return double.IsFinite(value)?value:null;
                }catch(Exception e) when(ReadError(e)){return null;}
            }
            var d=Value(hole,"Diameter",scale);var height=Value(hole,"Height",scale);
            if(d is not >0 || height is not >0)return null;
            double? chamfer=Value(hole,"ChamferSize",scale), thread=Value(hole,"ThreadOD",scale);
            var angle=Value(hole,"ChamferAngle");var tip=Value(hole,"TipAngle");var threadHeight=Value(hole,"ThreadHeight",scale);
            HoleFrame? frame=null;
            if(feature.Type.GetMethod("get_Lcs")!=null){
                try{
                    var matrix=feature.Call("get_Lcs",[]).Value!;
                    var axis=Point(matrix.GetType().GetField("vZ")!.GetValue(matrix)!);
                    var origin=Point(matrix.GetType().GetField("vT")!.GetValue(matrix)!)*scale;
                    if(axis.Length>0&&double.IsFinite(axis.Length)&&double.IsFinite(origin.Length))frame=new(origin,axis*(1/axis.Length));
                }catch(Exception e) when(ReadError(e)){ }
            }
            return new(feature.Text("get_Id"),d.Value,height.Value,Value(feature,"ZMin",scale),Value(feature,"ZMax",scale),
                hole.Count("get_IsBlind")!=0,Value(hole,"IsCrossHole") is double cross?cross!=0:null,
                tip is >=0 and <=180?tip:null,
                chamfer is >0?new(chamfer.Value,angle is >0 and <=180?angle:null):null,
                thread is >0?new(thread.Value,threadHeight is >0?threadHeight:null,Value(hole,"ThreadZMin",scale),Value(hole,"ThreadZMax",scale)):null,
                frame);
        }
        bool MatchesDiameter(HoleStage step) => !(diameter>0) || Math.Abs(step.DiameterMm-diameter)<1e-5;
        bool Visit(Handle f,int level,List<HoleStage>? group) {
            if(level>8 || --budget<0)throw new InvalidDataException("Hole feature tree is too large to verify.");
            if(f.Count("get_IsValid")==0)return false;
            var type=f.Call("get_FeatureType",[]).Value?.ToString();
            if(type=="caftComplexHole"){
                var stages=new List<HoleStage>();
                bool complete=true;
                for(int i=0,n=f.Count("get_SubFeatureCount");i<n;i++)complete &= Visit(f.Object("get_SubFeature",i),level+1,stages);
                if(stages.Any(MatchesDiameter)) {
                    var ordered=stages.DistinctBy(s=>s.FeatureId).ToList();
                    foreach(var s in ordered)compoundChildren.Add(s.FeatureId);
                    result.Add(new(path,f.Text("get_Id"),"compoundHole",ordered,complete));
                }
                return group==null && complete;
            }else if(type=="caftHole"){
                var s=Stage(f);if(s==null)return false;
                (group??simple).Add(s);return true;
            }else if(group==null){
                for(int i=0,n=f.Count("get_SubFeatureCount");i<n;i++)Visit(f.Object("get_SubFeature",i),level+1,null);
            }
            return false;
        }
        for(int i=0,n=list.Count("get_Count");i<n;i++)Visit(list.Object("get_Feature",i),0,null);
        foreach(var s in simple.DistinctBy(s=>s.FeatureId))
            if(!compoundChildren.Contains(s.FeatureId)&&MatchesDiameter(s))
                result.Add(new(path,s.FeatureId,"hole",[s],true));
        return result.DistinctBy(r=>r.FeatureId).ToList();
    }
}
