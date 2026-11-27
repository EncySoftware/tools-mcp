using ToolsMcp;
using Xunit;

public class ProjectToolAuditTests
{
    private static AuditCutter Drill(double diameter=10,double reach=50,double? angle=90, int position=71, string mag="Head")
        => new("71","10 mm drill","Drill",diameter,reach,angle,mag,position,"machineMagazine");

    [Fact] public void GroupsRepeatedHolesAndRejectsMixedDiameterForOneTool()
    {
        var review=ProjectToolAudit.Drill([new(10,40,"through"),new(10,40,"through"),new(12,30,"tip")],Drill());
        Assert.Equal("mismatch",review.Status);
        Assert.Equal(2,review.HoleGroups!.Length);
        Assert.Equal(2,review.HoleGroups[0].Count);
        Assert.Contains(new AuditReason("diameterMismatch",10,12),review.Reasons);
    }

    [Fact] public void MissingPointAngleAndMissingDepthStayUnverified()
    {
        Assert.Equal("fitsGeometry",ProjectToolAudit.Drill([new(10,40,"through")],Drill(reach:45)).Status);
        var shortFit=ProjectToolAudit.Drill([new(10,40,"through")],Drill(reach:44));
        Assert.Contains(new AuditReason("insufficientReach",44,45),shortFit.Reasons);
        Assert.Equal(45,shortFit.RequiredReachMm);
        Assert.Contains("pointAngle",ProjectToolAudit.Drill([new(10,40,"through")],Drill(angle:null)).Missing);
        Assert.Equal("needsInput",ProjectToolAudit.Drill([new(10,null,null)],Drill()).Status);
        Assert.Contains("assignedTool",ProjectToolAudit.Drill([new(10,40,"tip")],null).Missing);
    }

    [Fact] public void ManualContourLeavesClearanceUnknownAndSharesQuestionWithoutAssumingTheAnswer()
    {
        var brief = new OperationBrief("milling",null,null,12,null,null,null,null,null,null,0,20);
        var mill = Drill() with {Type="EndMill", ReachMm=8};
        var review = ProjectToolAudit.Mill(brief,mill);
        Assert.Equal("mismatch",review.Status);
        Assert.Contains("width",review.Missing);
        Assert.Contains(new AuditReason("insufficientReach",8,12),review.Reasons);
        var row=new ProjectAuditRow("op1","Contour","mill",null,"10",mill,review,brief);
        var questions=ProjectToolAudit.Questions([row,row with {OperationId="op2"}]);
        Assert.Equal(new[]{"op1","op2"},questions.Single(q=>q.Field=="width").OperationIds);
        Assert.Equal("unsupportedTool",ProjectToolAudit.Mill(brief,mill with {Type="Spherical"}).Status);
    }

    [Fact] public void SameNumberInTwoMagazinesRequiresDisambiguation()
    {
        var a=new LibraryAssembly("asm","T#71",null,[]);
        var item=new LibraryItem(null,"10 mm drill",null,true,null,null,null,null,null,a);
        var shelf = new Library("id","shelf",default,default,[item]);
        var tools=new[]{Drill(),Drill(mag:"Turret")};
        Assert.Equal("ambiguousPosition",Assert.Single(AuditOutput.Compare(shelf,tools)).Status);
        var scoped=shelf with {Items=[item with {Assembly=a with {Place="Turret"}}]};
        Assert.Equal("positionAndLabelAgree",Assert.Single(AuditOutput.Compare(scoped,tools)).Status);
        var elsewhere=new[]{Drill(),Drill(mag:"Turret",position:72)};
        Assert.Equal("numberInAnotherMagazine",Assert.Single(AuditOutput.Compare(scoped,elsewhere)).Status);
    }

    /// <summary>The place a technologist writes on the shelf is the magazine as ENCY's window names it, its caption.</summary>
    [Fact] public void APlaceWrittenAsTheMagazineCaptionFindsThatMagazine()
    {
        var a=new LibraryAssembly("asm","T#71",null,[]) with {Place="Upper turret"};
        var item=new LibraryItem(null,"10 mm drill",null,true,null,null,null,null,null,a);
        var shelf=new Library("id","shelf",default,default,[item]);
        var tools=new[]{Drill(mag:"Head") with {MagazineCaption="Lower turret"},Drill(mag:"Turret") with {MagazineCaption="Upper turret"}};
        var compared=Assert.Single(AuditOutput.Compare(shelf,tools));
        Assert.Equal("positionAndLabelAgree",compared.Status);
        Assert.Equal("Turret",compared.Candidates.Single(c=>c.SameMagazine).ProjectTool.Magazine);
    }

    [Fact] public void SetupSheetQuotesAndNeutralizesOperationNames()
    {
        var row=new ProjectAuditRow("op1","=run(\"x,y\")","drill",null,"71",Drill(),ProjectToolAudit.Drill([new(10,30,"tip")],Drill()));
        var report=new AuditReport(1,"part.stcp","metric",0,[row],[Drill()],[]);
        var serialized=System.Text.Json.JsonSerializer.Serialize(report, AuditOutput.Json);
        var fromIpc=System.Text.Json.JsonSerializer.Deserialize<AuditReport>(serialized,AuditOutput.Json)!;
        var text=System.Text.Encoding.UTF8.GetString(AuditOutput.SetupCsv(fromIpc));
        Assert.Contains("\"'=run(\"\"x,y\"\")\"",text);
        // a Russian Excel splits on ";" and reads a decimal comma; a decimal point could turn 10.5 into a date
        Assert.StartsWith("Machine;Setup;Operation;",text.TrimStart('﻿'));
        Assert.Contains("\"machineMagazine\";\"Head\";\"71\"",text);
        Assert.DoesNotContain("\",\"",text);
        var half=new AuditReport(1,"part.stcp","metric",0,[row with {AssignedTool=Drill() with {DiameterMm=10.5}}],[],[]);
        Assert.Contains("\"10,5\"",System.Text.Encoding.UTF8.GetString(AuditOutput.SetupCsv(half)));
    }
}
