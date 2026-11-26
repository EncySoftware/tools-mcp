using ToolsMcp;
using Xunit;

public class RectangularOpeningTests
{
    static readonly GeometryPoint[] Tilted = [
        new(7,10,2), new(7,14,2), new(10,14,6), new(10,10,6)
    ];

    [Fact] public void MeasuresInTheContourPlaneAndConvertsToMillimetres() {
        Assert.Equal(4, RectangularOpening.WidthMm(Tilted,true,18,1,new(4,0,-3))!.Value, 8);
        Assert.Equal(101.6, RectangularOpening.WidthMm([..Tilted, Tilted[0]],true,18,25.4)!.Value, 8);
        Assert.Equal(4, RectangularOpening.CurveWidthMm([Tilted[0],Tilted[0],new(7,12,2),..Tilted.Skip(1),Tilted[0]],
            true,18,1,new(4,0,-3))!.Value,8);
    }

    [Fact] public void RefusesUnprovenStraightnessOrMismatchedPlane() {
        Assert.Null(RectangularOpening.WidthMm(Tilted,false,18,1));
        Assert.Null(RectangularOpening.WidthMm(Tilted,true,18.2,1));
        Assert.Null(RectangularOpening.WidthMm(Tilted,true,18,1,new(0,0,1)));
        Assert.Null(RectangularOpening.WidthMm([..Tilted,new(8,10,2)],true,18,1));
        var skew = new GeometryPoint[]{new(0,0,0),new(4,0,0),new(6,2,0),new(2,2,0)};
        Assert.Null(RectangularOpening.WidthMm(skew,true,8 + 2*Math.Sqrt(8),1));
    }

    [Fact] public void OpenSlotNeedsTwoParallelOverlappingWallsAndAnAxialPlane() {
        GeometryPoint[] left = [new(1,0,0),new(1,20,0),new(1,20,4),new(1,0,4)];
        GeometryPoint[] right = [new(7,0,0),new(7,20,0),new(7,20,4),new(7,0,4)];
        Assert.Equal(6,RectangularOpening.WallGapMm(left,new(1,0,0),right,new(-1,0,0),new(0,0,1),1));
        Assert.Null(RectangularOpening.WallGapMm(left,new(1,0,0),right,new(1,0.01,0),new(0,0,1),1));
        Assert.Null(RectangularOpening.WallGapMm(left,new(1,0,0),right,new(-1,0,0),new(1,0,0),1));
        Assert.Null(RectangularOpening.WallGapMm(left,new(1,0,0),right.Select(p=>new GeometryPoint(p.X,p.Y+20,p.Z)).ToArray(),new(-1,0,0),new(0,0,1),1));
    }

    [Fact] public void RejectsDisconnectedEdgesAndOrdersReversedCoedges() {
        var edges = new[] { new RectangularOpening.Edge(Tilted[0],Tilted[1],4),
            new(Tilted[3],Tilted[2],4), new(Tilted[0],Tilted[3],5), new(Tilted[2],Tilted[1],5) };
        Assert.NotNull(RectangularOpening.ClosedRectangle(edges,new(4,0,-3),1));
        edges[1] = new(Tilted[3],Tilted[2],5); // arc with the same endpoints
        Assert.Null(RectangularOpening.ClosedRectangle(edges,new(4,0,-3),1));
        edges[1] = new(Tilted[3],new(10,16,6),4);
        Assert.Null(RectangularOpening.ClosedRectangle(edges,new(4,0,-3),1));
    }
}
