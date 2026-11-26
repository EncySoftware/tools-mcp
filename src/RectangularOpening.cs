namespace ToolsMcp;

internal readonly record struct GeometryPoint(double X, double Y, double Z)
{
    public static GeometryPoint operator -(GeometryPoint a, GeometryPoint b) => new(a.X-b.X, a.Y-b.Y, a.Z-b.Z);
    public static GeometryPoint operator *(GeometryPoint a, double k) => new(a.X*k, a.Y*k, a.Z*k);
    internal double Dot(GeometryPoint b) => X*b.X + Y*b.Y + Z*b.Z;
    internal double Length => Math.Sqrt(Dot(this));
    internal GeometryPoint Cross(GeometryPoint b) => new(Y*b.Z-Z*b.Y, Z*b.X-X*b.Z, X*b.Y-Y*b.X);
}

internal static class RectangularOpening
{
    internal readonly record struct Edge(GeometryPoint Start, GeometryPoint End, double Length);

    internal static double? CurveWidthMm(IReadOnlyList<GeometryPoint> knots, bool closed, double perimeter,
        double unitToMm, GeometryPoint normal)
    {
        if (!closed || knots.Count < 4 || knots.Count > 96 || unitToMm <= 0 || !double.IsFinite(unitToMm)) return null;
        var points = knots.ToList();
        var tolerance = Math.Max(1e-7 / unitToMm, perimeter*1e-10);
        // ENCY also emits repeated knots and intermediate points along a straight CAD edge.
        for (int i = points.Count - 1; i >= 0 && points.Count > 1; i--)
            if ((points[i] - points[(i+1)%points.Count]).Length <= tolerance) points.RemoveAt(i);
        while (points.Count > 4) {
            int flat = -1;
            for (int i=0; i<points.Count; i++) {
                var from = points[i] - points[(i+points.Count-1)%points.Count];
                var to = points[(i+1)%points.Count] - points[i];
                if (from.Dot(to) > 0 && from.Cross(to).Length <= tolerance * Math.Min(from.Length,to.Length)) {
                    flat = i;
                    break;
                }
            }
            if (flat < 0) return null;
            points.RemoveAt(flat);
        }
        return WidthMm(points,true,perimeter,unitToMm,normal);
    }

    internal static GeometryPoint[]? ClosedRectangle(IReadOnlyList<Edge> edges, GeometryPoint faceNormal, double scale)
    {
        if (edges.Count != 4 || edges.Any(e => !(e.Length > 0) || !double.IsFinite(e.Length)) ||
            !(scale > 0) || !double.IsFinite(scale)) return null;
        var ordered = new List<GeometryPoint> { edges[0].Start, edges[0].End };
        var remaining = edges.Skip(1).ToList();
        double tolerance = Math.Max(1e-7 / scale, edges.Max(e => e.Length)*1e-9);
        while (remaining.Count > 0) {
            var matches = remaining.Where(e => (e.Start - ordered[^1]).Length <= tolerance ||
                                               (e.End - ordered[^1]).Length <= tolerance).ToList();
            if (matches.Count != 1) return null;
            var e = matches[0];
            ordered.Add((e.Start-ordered[^1]).Length <= tolerance ? e.End : e.Start);
            remaining.Remove(e);
        }
        return WidthMm(ordered, true, edges.Sum(e => e.Length), scale, faceNormal) != null
            ? ordered.Take(4).ToArray() : null;
    }

    internal static double? WallGapMm(GeometryPoint[] first, GeometryPoint n1, GeometryPoint[] second,
        GeometryPoint n2, GeometryPoint axis, double scale)
    {
        if (first.Length != 4 || second.Length != 4) return null;
        var n = n1*(1/n1.Length);
        var other = n2*(1/n2.Length);
        var z = axis*(1/axis.Length);
        if (!(n.Cross(other).Length <= 1e-8) || !(Math.Abs(n.Dot(z)) <= 1e-8)) return null;
        bool AxialRectangle(GeometryPoint[] loop) {
            var side = loop[1] - loop[0];
            var direction = side * (1/side.Length);
            return Math.Abs(direction.Dot(z)) <= 1e-8 || direction.Cross(z).Length <= 1e-8;
        }
        if (!AxialRectangle(first) || !AxialRectangle(second)) return null;
        double Overlap(GeometryPoint direction) {
            var a = first.Select(p => p.Dot(direction)).ToArray();
            var b = second.Select(p => p.Dot(direction)).ToArray();
            return (Math.Min(a.Max(), b.Max()) - Math.Max(a.Min(), b.Min()))*scale;
        }
        // Both faces must share a longitudinal and axial interval. Parallel extensions
        // outside the feature's actual walls do not constrain its cutter.
        if (!(Overlap(z)>1e-6 && Overlap(z.Cross(n))>1e-6)) return null;
        var gap = Math.Abs((first[0]-second[0]).Dot(n))*scale;
        return double.IsFinite(gap) && gap>1e-6 ? gap : null;
    }
    // Each pair of adjacent knots is joined by a curve at least as long as its chord.
    // A matching measured perimeter excludes rounded corners, arcs and curved sides.
    // Work in 3D so a tilted or translated machining plane gives the same clearance.
    internal static double? WidthMm(IReadOnlyList<GeometryPoint> vertices, bool closed, double perimeter,
        double unitToMm, GeometryPoint? normal = null)
    {
        if (!closed || vertices.Count is < 4 or > 5 || !double.IsFinite(perimeter) || perimeter <= 0 ||
            !double.IsFinite(unitToMm) || unitToMm <= 0) return null;
        var p = vertices.Select(v => v * unitToMm).ToArray();
        var edges = Enumerable.Range(0, 4).Select(i => p[(i+1)%4] - p[i]).ToArray();
        var lengths = edges.Select(edge => edge.Length).ToArray();
        if (lengths.Any(v => !double.IsFinite(v) || v <= 0)) return null;
        var tolerance = Math.Max(1e-7, lengths.Max() * 1e-9);
        if (lengths.Min() < 100*tolerance || Math.Abs(lengths.Sum() - perimeter*unitToMm) > tolerance ||
            p.Length == 5 && !((p[4] - p[0]).Length <= tolerance)) return null;

        var unit = edges.Select((v,i) => v * (1/lengths[i])).ToArray();
        const double angularTolerance = 1e-8;
        if (Math.Abs(unit[0].Dot(unit[1])) > angularTolerance ||
            unit[0].Cross(unit[2]).Length > angularTolerance || unit[0].Dot(unit[2]) >= 0 ||
            unit[1].Cross(unit[3]).Length > angularTolerance || unit[1].Dot(unit[3]) >= 0 ||
            Math.Abs(lengths[0]-lengths[2]) > tolerance || Math.Abs(lengths[1]-lengths[3]) > tolerance)
            return null;
        if (normal is { } n) {
            double len = n.Length;
            if (!(len > 0) || !double.IsFinite(len) ||
                unit.Take(2).Any(side => Math.Abs(side.Dot(n)/len) > angularTolerance)) return null;
        }
        return lengths.Min();
    }
}
