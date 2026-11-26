using ToolsMcp;
using Xunit;

public class OperationAnswerStoreTests
{
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
