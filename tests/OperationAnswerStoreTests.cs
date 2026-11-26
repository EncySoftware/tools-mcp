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

    [Fact] public async Task SelectedSearchSkipsAuditUntilThisOperationHasAnAnswer()
    {
        var dir=Directory.CreateTempSubdirectory("selected-answers-");
        try {
            string path=Path.Combine(dir.FullName,"part.stcp");File.WriteAllText(path,"saved");
            var mem=new OperationAnswerStore(Path.Combine(dir.FullName,"memory"));
            var op=new ProjectAuditRow("target","Slot","TST2DContouringOp",null,null,null,
                new("needsInput",["width"],[]),new(Operation:"slot",Depth:4,NoInsideCorner:true));
            var report=new AuditReport(57,path,"metric",0,[op],[],[],ProjectModified:false,SelectedOperationId:"target");
            var none=await mem.ForSelection(path,"target",57,"metric",()=>throw new Exception("unexpected audit"));
            Assert.Null(none.Answer);Assert.Null(none.Error);
            Assert.StartsWith("Remembered",mem.Remember(report,new("target",WidthMm:8)));
            Assert.True(mem.HasAnswer("TARGET"));
            var hit=await mem.ForSelection(path,"target",57,"metric",()=>Task.FromResult<AuditReport?>(report));
            Assert.Equal(8,hit.Answer!.WidthMm);
            foreach(var other in new[]{report with{SelectedOperationId="another"},report with{ProcessId=1},report with{ProjectPath=path+".other"},report with{OperationUnits="imperial"}}) {
                var mismatch=await mem.ForSelection(path,"target",57,"metric",()=>Task.FromResult<AuditReport?>(other));
                Assert.Null(mismatch.Answer);Assert.StartsWith("ERROR:",mismatch.Error);
            }
            var modified=await mem.ForSelection(path,"target",57,"metric",()=>Task.FromResult<AuditReport?>(report with{ProjectModified=true}));
            Assert.Null(modified.Answer);
            File.AppendAllText(path,"changed");
            var stale=await mem.ForSelection(path,"target",57,"metric",()=>Task.FromResult<AuditReport?>(report));
            Assert.Null(stale.Answer);
        } finally {dir.Delete(true);}
    }

    [Fact] public void SavedAnswersOnlySupplyUnresolvedFields()
    {
        var evidence=new List<string>();
        var saved=new ProjectPlanGeometry("op","pocket",WidthMm:15,DepthMm:11,InnerRadiusMm:4,PassDepthMm:2,BottomRadiusMm:1);
        var known=new OperationBrief(Operation:"pocket",Width:10,Depth:12,NoInsideCorner:true,BottomRadius:0) {
            WidthSource="rectangularInsideContour",BottomRadiusSource="feature"
        };
        var result=OperationAnswerStore.FillMissing(known,saved,false,evidence);
        Assert.Equal(10,result.Width);Assert.Equal("rectangularInsideContour",result.WidthSource);
        Assert.Equal(12,result.Depth);Assert.Null(result.InnerRadius);Assert.Equal(0,result.BottomRadius);
        Assert.Equal(2,result.PassDepth);Assert.Equal(new[]{"passDepth"},evidence);
        evidence.Clear();
        var missing=OperationAnswerStore.FillMissing(new(Operation:"milling",Depth:5),saved,false,evidence);
        Assert.Equal("remembered",missing.WidthSource);Assert.Equal("remembered",missing.BottomRadiusSource);
        Assert.Equal("pocket",missing.Operation);Assert.Equal(5,missing.Depth);Assert.Equal(15,missing.Width);
        evidence.Clear();
        var explicitWidth=OperationAnswerStore.FillMissing(new(Operation:"slot",Width:6,PassDepth:3){WidthSource="user"},saved,false,evidence);
        Assert.Equal(6,explicitWidth.Width);Assert.Equal("user",explicitWidth.WidthSource);Assert.Equal(3,explicitWidth.PassDepth);
    }

    [Fact] public void RememberedBreakthroughRespectsExplicitZeroAndBlindHoles()
    {
        var saved=new ProjectPlanGeometry("op","through",WidthMm:8,BreakthroughMm:1);
        var through=new OperationBrief(Operation:"drilling",Diameter:7,Bottom:"through",Breakthrough:0);
        Assert.Equal(1,OperationAnswerStore.FillMissing(through,saved,false,[]).Breakthrough);
        Assert.Equal(0,OperationAnswerStore.FillMissing(through,saved,true,[]).Breakthrough);
        var blind=OperationAnswerStore.FillMissing(through with {Bottom="blind"},saved,false,[]);
        Assert.Equal("blind",blind.Bottom);Assert.Equal(7,blind.Diameter);Assert.Equal(0,blind.Breakthrough);
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
