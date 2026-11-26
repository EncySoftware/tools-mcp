using System.Text;
using System.Xml.Linq;
using ToolsMcp;
using Xunit;

public class OperationUnitsTests
{
    [Theory]
    [InlineData("Metric","metric")]
    [InlineData("Imperial","imperial")]
    [InlineData("Guess",null)]
    public void AcceptsOnlyNamedUnitsAndTheRequestedOperation(string marker,string? expected)
    {
        Guid id=Guid.NewGuid();
        var tree=new XElement("SCTechOpList",new XElement("Operations",new XElement("SCTechOperation",new XElement("InstanceID",id))));
        var snapshot=new XElement("SCProject",new XElement("UnitsSystem",marker));
        Assert.Equal(expected,EopUnits.ForOperation(snapshot,tree,id));
        Assert.Null(EopUnits.ForOperation(snapshot,tree,Guid.NewGuid()));
        Assert.Null(EopUnits.ForOperation(new XElement("SCProject",new XElement("NewProjectUnits","Metric")),tree,id));
    }

    [Fact] public void GroupsByAssignmentEvenWhenDifferentNodesHaveDifferentRecognition()
    {
        var a=new HoleReading(6,14,"through",Recognized:[new("Part\\A","fa","compoundHole",[],false)]);
        var b=new HoleReading(6,14,"through",Recognized:[new("Part\\B","fb","hole",[],true)]);
        var g=Assert.Single(ProjectToolAudit.Drill([a,b],null).HoleGroups!);
        Assert.Equal(2,g.Count);
    }

    [Fact] public void DoesNotPlanAnIndividualSelectedStageTwice()
    {
        var stage=new HoleStage("step",10,20,0,20,false,false,null,new(1,45),new(12,16,-16,0),null);
        var parent=new RecognizedHole("CAD face","parent","compoundHole",[stage],true);
        var standalone=new RecognizedHole("feature tree","step","hole",[stage],true);
        var actual=Assert.Single(HoleSelections.Combine([standalone,parent,parent]));
        Assert.Equal("parent",actual.FeatureId);
        Assert.Equal(12,actual.Steps[0].Thread!.OuterDiameterMm);
        Assert.Equal(45,actual.Steps[0].EntranceChamfer!.AngleDegrees);
    }
}
