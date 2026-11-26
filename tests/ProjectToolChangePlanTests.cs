using ToolsMcp;
using Xunit;

public class ProjectToolChangePlanTests
{
    private static ProjectToolChange Change() => new(Guid.NewGuid().ToString(),"10","new-guid","slot",16,8,"projectToolList",NoInsideCorner:true);
    private static ToolBinding Binding(string id) => new(id,id+"-guid","projectToolList","tool "+id,"EndMill","0",1,12,30,null,[]);
    private static ToolChangeRow Row(ProjectToolChange c) => new(c,"Contour","verified-geometry",Binding("old"),Binding("new"),null);
    private static ToolPlanRequest Request(params ToolChangeRow[] rows) => new("sample.stcp","metric",rows.Select(r=>r.Change).ToArray());
    private static ToolPlanRequest Confirm(ToolPlanRequest p,params ToolChangeRow[] rows) =>
        p with {Confirmed=true,PreviewId=ProjectToolChangePlan.Fingerprint(1,p,rows)};

    [Fact] public void RefusesRepeatedTargetsAndAmbiguousMillingRadius() {
        var c=Change();var r=Row(c);
        Assert.Null(ProjectToolChangePlan.Validate(Request(r)));
        Assert.NotNull(ProjectToolChangePlan.Validate(Request(r,Row(c with {OperationId="{"+c.OperationId+"}"}))));
        Assert.NotNull(ProjectToolChangePlan.Validate(Request(Row(c with {NoInsideCorner=false}))));
        Assert.NotNull(ProjectToolChangePlan.Validate(Request(Row(c with {WidthMm=double.NaN}))));
    }

    [Fact] public void StaleOrUnconfirmedPlanNeverCallsSetter() {
        var r=Row(Change());var p=Request(r);
        Assert.Equal("confirmationRequired",ProjectToolChangePlan.Apply(p,1,[r],_=>throw new Exception(),_=>throw new Exception()).Status);
        int calls=0;
        foreach(var changed in new[]{r with {GeometryStamp="edited"},r with {After=r.After! with {ReachMm=9}},
                    r with {Before=r.Before! with {Identity="other-tool"}},r with {Problem="missing"}}) {
            var result=ProjectToolChangePlan.Apply(Confirm(p,r),1,[changed],_=>changed,_=>calls++);
            Assert.Equal("stalePreview",result.Status);
        }
        Assert.Equal(0,calls);
    }

    [Fact] public void AgentConfirmationCannotOverrideOperatorRefusal()
    {
        var a=Row(Change()); var b=Row(Change());
        var result=ProjectToolChangePlan.Apply(Confirm(Request(a,b),a,b),1,[a,b],
            _=>throw new Exception("unexpected reread"),_=>throw new Exception("unexpected setter"),approve:()=>false);
        Assert.Equal("notApplied",result.Status);
        Assert.Equal(2,result.Outcomes.Count);
        Assert.All(result.Outcomes,o=>Assert.Equal("notApplied",o.Status));
    }

    [Fact] public void ReportsPartialApplicationAndSkipsRemainingOnError() {
        var a=Row(Change());var b=Row(Change());var c=Row(Change());
        var rows=new[]{a,b,c};var current=rows.ToDictionary(r=>r.Change.OperationId);
        var result=ProjectToolChangePlan.Apply(Confirm(Request(rows),rows),1,rows,
            choice=>current[choice.OperationId], row=>{
                if(row==b)throw new InvalidOperationException("tool changed");
                current[row.Change.OperationId]=row with {Before=row.After};
            });
        Assert.Equal("partial",result.Status);
        Assert.Equal(new[]{"applied","failed","skipped"},result.Outcomes.Select(o=>o.Status));
    }

    [Fact] public void ReportsAWriteThatSucceededBeforeNativeError() {
        var row=Row(Change());var current=row;
        var result=ProjectToolChangePlan.Apply(Confirm(Request(row),row),1,[row],_=>current,r=>{
            current=r with {Before=r.After};throw new InvalidOperationException("late native error");
        });
        Assert.Equal("appliedWithWarning",Assert.Single(result.Outcomes).Status);
        Assert.Equal("partial",result.Status);
    }

    [Fact] public void UnreadablePostWriteStateIsReportedAsUnknown() {
        var row=Row(Change());bool wrote=false;
        var result=ProjectToolChangePlan.Apply(Confirm(Request(row),row),1,[row],
            _=>wrote?row with {Before=null,After=null,Problem="Operation unavailable."}:row,
            _=>wrote=true);
        Assert.Equal("partial",result.Status);
        Assert.Equal("stateUnknown",Assert.Single(result.Outcomes).Status);
    }

    [Fact] public void InterruptedResponseRetainsCompletedWorkAndMarksInFlightUnknown()
    {
        var rows=new[]{Row(Change()),Row(Change()),Row(Change())};
        string Line(int i,string status)=>ProjectToolChangePlan.CheckpointPrefix+
            System.Text.Json.JsonSerializer.Serialize(new ChangeOutcome(rows[i].Change.OperationId,status),AuditOutput.Json);
        var result=System.Text.Json.Nodes.JsonNode.Parse(ProjectToolChangePlan.Interrupted(Request(rows),
            string.Join("\n",Line(0,"applied"),Line(1,"starting"),"native error")))!;
        Assert.Equal("interrupted",(string?)result["status"]);
        Assert.Equal(new[]{"applied","stateUnknown","notApplied"},result["outcomes"]!.AsArray().Select(n=>(string?)n!["status"]));
    }

    [Fact] public void SimulationReplayRequiresTheCompletePreviewPair() {
        var request=new SimulationRequest("part.stcp","metric",null);
        Assert.True(request.Valid());
        Assert.False((request with {Changes=[Change()]}).Valid());
        Assert.False((request with {PreviewId="old"}).Valid());
        Assert.False((request with {SnapshotPath="relative/snapshot.stcp"}).Valid());
        Assert.True((request with {Changes=[Change()],PreviewId="id"}).Valid());
    }

    [Fact] public void SimulationNeverReportsAStaleUnverifiedOrIncompleteResultAsClear() {
        var e=new SimulationEvidence(true,true,true,true,true,true,true,[]);
        Assert.Equal("noErrorsReported",e.Status);
        Assert.Equal("errorsDetected",(e with {Errors=["Holder"]}).Status);
        Assert.Equal("incomplete",(e with {Simulated=false}).Status);
        Assert.Equal("unverified",(e with {CachedToolpathCleared=false}).Status);
        Assert.Equal("unverified",(e with {AssemblyVerified=false}).Status);
        Assert.Equal("unverified",(e with {MachineChecks=false}).Status);
    }
}
