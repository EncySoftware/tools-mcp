using System.Text.Json;
using System.Text.Json.Nodes;
using ToolsMcp;
using Xunit;

public class SharedToolPlanTests
{
    static PlanRow Row(string id,string machine="mill") => new(new(id,new(Operation:"slot",Width:10,Depth:5,NoInsideCorner:true),machine));
    static PlanHit Hit(string id,string source="catalogue",string? insert=null) =>
        new(new(source,id,null,null,null,insert,null,null),id,10,5,"fit");

    [Fact] public void ReusesTheSamePhysicalCombinationInsteadOfSelectingFirstHitIndependently()
    {
        var a=Row("rough"); var b=Row("finish");
        a.Hits.AddRange([Hit("A"),Hit("B")]); b.Hits.AddRange([Hit("C"),Hit("B")]);
        var result=JsonSerializer.SerializeToNode(SharedToolPlan.Select([a,b]),AuditOutput.Json)!;
        Assert.Equal(1,(int)result["distinctTools"]!);
        Assert.Equal(2,(int)result["individualSelection"]!["distinctTools"]!);
        Assert.Equal("B",a.Selected!.Key.ToolId);Assert.Equal("B",b.Selected!.Key.ToolId);
        Assert.Equal(0,(int)result["changesInGivenOrder"]!);
    }
    [Fact] public void SeparatesMachinesInsertIdentitiesAndUnavailableOperations()
    {
        var a=Row("a");var b=Row("b");var c=Row("c","lathe2");var d=Row("d");
        a.Hits.Add(Hit("holder",insert:"left"));b.Hits.Add(Hit("holder",insert:"right"));
        c.Hits.Add(Hit("holder",insert:"left"));d.Questions.Add("width");
        var result=JsonSerializer.SerializeToNode(SharedToolPlan.Select([a,b,c,d]),AuditOutput.Json)!;
        Assert.Equal(3,(int)result["distinctTools"]!);Assert.Null(result["changesInGivenOrder"]);
        Assert.Equal("needs_input",(string?)result["operations"]![3]!["status"]);
    }
    [Fact] public void PrefersInstalledToolsAndRetainsAlreadyAssignedAmongEqualChoices()
    {
        var a=Row("a"); var b=Row("b");
        a.Hits.AddRange([Hit("common"),Hit("unused","machineMagazine"),Hit("already","machineMagazine") with {AlreadyAssigned=true}]);
        b.Hits.Add(Hit("common")); SharedToolPlan.Select([a,b]);
        Assert.Equal("already",a.Selected!.Key.ToolId);Assert.Equal("common",b.Selected!.Key.ToolId);
    }

    [Fact] public void ProjectPlanKeepsTheSourceOfTheWidthActuallyUsed()
    {
        var milling = new OperationBrief(Operation:"slot", Width:12, Depth:4, NoInsideCorner:true)
            { WidthSource="rectangularInsideContour" };
        var review=new GeometryReview("needsInput",["assignedTool"],[]);
        var a=new ProjectAuditRow("one","Pocket","TSTPocketingOp",null,null,null,review,milling);
        var audit=new AuditReport(1,"test.stcp","metric",0,[a,a with {OperationId="two"}],[],[]);
        var rows=ProjectToolPlan.Rows(audit,new Dictionary<string,ProjectPlanGeometry>{{"two",new("two",WidthMm:10)}});
        Assert.Equal("rectangularInsideContour",rows[0].Step.Geometry.WidthSource);
        Assert.Equal("user",rows[1].Step.Geometry.WidthSource);
        Assert.Equal(10,rows[1].Step.Geometry.Width);
    }

    [Fact] public void ProjectPlannerUsesTheKnownMagazineAndRejectsConflictingHoleDimensions()
    {
        var drill=new AuditCutter("42","D10","Drill",10,48,118,"AxisS",2,"machineMagazine",Identity:"guid");
        var tooShort=drill with {Id="43",ReachMm=41};var elsewhere=drill with {Id="44",Magazine="Turret"};
        var review=new GeometryReview("fitsGeometry",[],[],[new(10,40,"through",false,2)]);
        var first=new ProjectAuditRow("one","Drill","HoleMachiningOp",null,"42",drill,review,Machine:"MX");
        var second=first with {OperationId="two"};
        var audit=new AuditReport(1,"test.stcp","metric",0,[first,second],[drill,tooShort,elsewhere],[]);
        var rows=ProjectToolPlan.Rows(audit,new Dictionary<string,ProjectPlanGeometry> {["two"]=new("two",WidthMm:8)});
        Assert.Equal("42",Assert.Single(rows[0].Hits).Key.ToolId);
        Assert.True(rows[0].Hits[0].AlreadyAssigned);
        Assert.NotNull(rows[1].Error); Assert.Empty(rows[1].Hits);
    }
}
