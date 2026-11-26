using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;

namespace ToolsMcp;

internal static partial class EncyIpc
{
    private sealed record OpeningWidth(double WidthMm, string Source);
    private sealed record PlanarRectangle(GeometryPoint[] Corners, GeometryPoint Normal, double WidthMm);

    private static GeometryPoint Point(object value) {
        double Number(string key) => Convert.ToDouble(value.GetType().GetField(key)!.GetValue(value), CultureInfo.InvariantCulture);
        return new(Number("X"), Number("Y"), Number("Z"));
    }

    private static PlanarRectangle? ReadRectangle(Handle geom, Type faceType, string fullName, double scale)
    {
        var entity = geom.Object("FindByFullName",fullName).Object("get_GeometryEntity");
        if (!faceType.IsInstanceOfType(entity.Value)) return null;
        var face = new Handle(entity.Value,faceType).Object("get_Face");
        object?[] plane = [null, null];
        if (!Equals(face.Call("GetPlane",plane).Value, true)) return null;
        var normal = Point(plane[1]!);
        var loop = face.Object("GetFirstLoop");
        if (loop.Count("get_IsOuter") == 0 || loop.Call("GetNext",[]).Value != null) return null;
        var iter = loop.Object("get_CoEdges");
        iter.Call("Reset",[]);
        var edges = new List<RectangularOpening.Edge>();
        while (iter.Count("Next") != 0) {
            if (edges.Count == 4) return null;
            var edge = iter.Object("Current").Object("get_Edge");
            edges.Add(new(Point(edge.Call("get_StartPoint",[]).Value!), Point(edge.Call("get_EndPoint",[]).Value!),
                Convert.ToDouble(edge.Call("get_Length",[]).Value, CultureInfo.InvariantCulture)));
        }
        var corners = RectangularOpening.ClosedRectangle(edges, normal, scale);
        if (corners == null) return null;
        double width = Math.Min((corners[1]-corners[0]).Length, (corners[2]-corners[1]).Length)*scale;
        return new(corners,normal,width);
    }

    private static GeometryPoint ToolAxis(Handle op) {
        var evaluator = op.Object("get_Machine").Object("CreateEvaluator");
        op.Call("InitMachineEvaluator",[evaluator.Value]);
        var matrix = evaluator.Call("GetAbsoluteMatrix",[]).Value!;
        return Point(matrix.GetType().GetField("vZ")!.GetValue(matrix)!);
    }

    private static OpeningWidth? CurveWidth(Handle geom, Type curveType, string path, double scale, GeometryPoint axis)
    {
        var entity = geom.Object("FindByFullName",path).Object("get_GeometryEntity");
        if (!curveType.IsInstanceOfType(entity.Value)) return null;
        var curve = new Handle(entity.Value,curveType).Object("get_Curve");
        int count = curve.Count("get_QntP");
        if (count is < 3 or > 96 || curve.Count("get_IsClosed") == 0) return null;
        var points = Enumerable.Range(0,Math.Max(4,count)).Select(i=>Point(curve.Call("get_KnotPoint",[i]).Value!)).ToArray();
        double perimeter = Convert.ToDouble(curve.Call("get_FullLen",[]).Value,CultureInfo.InvariantCulture);
        var width = RectangularOpening.CurveWidthMm(points,true,perimeter,scale,axis);
        return width is double w ? new(w,"rectangularInsideContour") : null;
    }

    private static OpeningWidth? FeatureWidth(Handle feature, Handle geom, Type faceType, string assignedNode, double scale,
        GeometryPoint toolAxis)
    {
        string? kind = feature.Call("get_FeatureType",[]).Value?.ToString();
        if (kind is not ("caftPocket" or "caft25DPocket" or "caft25DSlot") || feature.Count("get_IsValid") == 0 ||
            feature.Count("get_SubFeatureCount") != 0) return null;
        bool slot = kind == "caft25DSlot";
        int count = feature.Count("get_BaseEntityCount");
        if (count < 1 || count > (slot ? 3 : 1)) return null;

        var matrix = feature.Call("get_Lcs",[]).Value!;
        var z = Point(matrix.GetType().GetField("vZ")!.GetValue(matrix)!);
        if (!(z.Length > 0) || !double.IsFinite(z.Length)) return null;
        var axis = z*(1/z.Length);
        if (!(axis.Cross(toolAxis*(1/toolAxis.Length)).Length <= 1e-8)) return null;
        string parent = assignedNode[..(assignedNode.LastIndexOf('\\')+1)];
        var names = Enumerable.Range(0,count).Select(i=>feature.Text("get_BaseEntityName",i))
            .Select(n => n.Contains('\\') ? n : parent+n).ToArray();
        if (!names.Contains(assignedNode, StringComparer.OrdinalIgnoreCase) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != count) return null;
        var walls = new List<PlanarRectangle>();
        foreach (var name in names) {
            var face = ReadRectangle(geom, faceType, name, scale);
            if (face == null) return null;
            var unit = face.Normal*(1/face.Normal.Length);
            if (unit.Cross(axis).Length <= 1e-8) {
                if (count == 1) return new(face.WidthMm,"rectangularPocketFloor");
            } else if (slot && Math.Abs(unit.Dot(axis)) <= 1e-8) {
                walls.Add(face);
            } else return null;
        }
        if (walls.Count != 2) return null;
        var gap = RectangularOpening.WallGapMm(walls[0].Corners, walls[0].Normal, walls[1].Corners, walls[1].Normal, axis, scale);
        return gap is double width ? new(width, "parallelWallsOfRecognizedSlot") : null;
    }

    private static OpeningWidth? ReadOpeningWidth(Handle op, Handle project, Handle item, AssemblyLoadContext runtime, double scale)
    {
        try {
            string path = item.Text("get_Caption");
            if (!path.Contains('\\')) return null;
            var model = runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.ModelFormerTypes"));
            var pocketType = model.GetType("CAMIPC.ModelFormerTypes.ICamIpcPocketModelItem",true)!;
            var areaType = model.GetType("CAMIPC.ModelFormerTypes.ICamIpcAreaModelItem",true)!;
            var geomTypes=runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.GeomModel"));
            var geom = project.Object("get_GeomModel");
            if (areaType.IsInstanceOfType(item.Value)) {
                var area = new Handle(item.Value,areaType);
                if (area.Call("get_Mode",[]).Value?.ToString() != "apmAddArea" ||
                    Convert.ToDouble(area.Call("get_Stock",[]).Value,CultureInfo.InvariantCulture) != 0) return null;
                return CurveWidth(geom,geomTypes.GetType("CAMIPC.GeomModel.ICamIpcCurveGeometryEntity",true)!,
                    path,scale,ToolAxis(op));
            }
            if (!pocketType.IsInstanceOfType(item.Value)) return null;
            var pocket = new Handle(item.Value,pocketType);
            if (pocket.Call("get_ContactMode",[]).Value?.ToString() != "ajcmInside" ||
                Convert.ToDouble(pocket.Call("get_Stock",[]).Value, CultureInfo.InvariantCulture) != 0) return null;
            var faceType=geomTypes.GetType("CAMIPC.GeomModel.ICamIpcFaceGeometryEntity",true)!;
            var finder = project.Object("get_FeatureFinder");
            var matrix = finder.Type.GetMethod("GetFeaturesForNode")!.GetParameters()[2].ParameterType;
            if (matrix.IsByRef) matrix = matrix.GetElementType()!;
            var matches = finder.Object("GetFeaturesForNode",path,false,Activator.CreateInstance(matrix));
            var results = new List<OpeningWidth>();
            GeometryPoint? toolAxis = null;
            for(int i=0;i<matches.Count("get_Count");i++){
                var feature = matches.Object("get_Feature",i);
                if(feature.Call("get_FeatureType",[]).Value?.ToString() is not ("caftPocket" or "caft25DPocket" or "caft25DSlot")) continue;
                toolAxis ??= ToolAxis(op);
                var reading = FeatureWidth(feature,geom,faceType,path,scale,toolAxis.Value);
                if(reading == null)return null;
                results.Add(reading);
            }
            return results.Count > 0 && results.All(r => r == results[0]) ? results[0] : null;
        } catch(Exception e) when (ReadError(e) || e is MissingMemberException or InvalidCastException) {
            return null;
        }
    }
}
