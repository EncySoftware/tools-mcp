using ToolsMcp;
using Xunit;

public class OperationAnswerStoreTests
{
    [Fact] public void OneSetupAnswerFillsMissingFieldsWithoutReplacingKnownOperationMaterial()
    {
        var op=new ProjectAuditRow("a","Pocket","TSTPocketingOp","steel",null,null,
            new("needsInput",["width"],[]),new(Operation:"slot",Depth:5,NoInsideCorner:true,Material:"steel"),Machine:"VMC");
        var other=op with {OperationId="b",Material=null,MillingGeometry=op.MillingGeometry! with {Material=null}};
        var audit=new AuditReport(1,"test.stcp","metric",0,[op,other],[],[]);
        var rows=ProjectToolPlan.Rows(audit,new Dictionary<string,ProjectPlanGeometry>(),
            new Dictionary<string,ProjectSetupAnswer>{{"VMC",new("VMC","aluminium","HSK-A63")}});
        Assert.Equal("steel",rows[0].Step.Geometry.Material);
        Assert.Equal("aluminium",rows[1].Step.Geometry.Material);
        Assert.All(rows,r=>Assert.Equal("HSK-A63",r.Step.Geometry.Spindle));
        var plan=System.Text.Json.Nodes.JsonNode.Parse(ProjectToolPlan.Result(audit,rows,true))!;
        var q=Assert.Single(plan["questions"]!.AsArray());
        Assert.Equal("width",(string?)q!["field"]);
        Assert.Equal(2,q["operations"]!.AsArray().Count);
    }

    [Fact] public void ReusesOnlyTheSameSavedProjectAndJobGeometry()
    {
        var dir=Directory.CreateTempSubdirectory("project-answers-");
        try {
            string path=Path.Combine(dir.FullName,"part.stcp");File.WriteAllText(path,"version1");
            var mem=new OperationAnswerStore(Path.Combine(dir.FullName,"answers"));
            var op=new ProjectAuditRow("id","Contour","TST2DContouringOp","steel",null,null,
                new("needsInput",["width"],[]),new(Operation:"slot",Depth:5,NoInsideCorner:true));
            var report=new AuditReport(1,path,"metric",0,[op],[],[],ProjectModified:false);
            Assert.StartsWith("Remembered",mem.Remember(report,new("id",WidthMm:10)));
            Assert.Equal(10,mem.Read(report)["id"].WidthMm);
            Assert.Empty(mem.Read(report with {ProjectModified=true}));
            Assert.Empty(mem.Read(report with {Operations=[op with {MillingGeometry=op.MillingGeometry! with {Depth=15}}]}));
            File.WriteAllText(path,"version2");Assert.Empty(mem.Read(report));
            Assert.StartsWith("Remembered",mem.Remember(report,new("id",WidthMm:8)));
            mem.Forget("id");Assert.Empty(mem.Read(report));
        } finally {dir.Delete(true);}
    }
}
