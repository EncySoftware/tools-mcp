using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ToolsMcp;

public sealed record CuttingPass(string? Material=null,string? Operation=null,double? ApMm=null,double? AeMm=null,
    string? Coolant=null,string? Machine=null,string? Assembly=null,double? CuttingDiameterMm=null,int? Teeth=null,
    double? MachineMaxRpm=null,double? AssemblyMaxRpm=null,double? MachineMaxFeedMmMin=null,double? AssemblyMaxFeedMmMin=null,
    int Offset=0,int Size=20,int? OnlyCondition=null,string? LibraryId=null,string? AssemblyId=null);
public sealed record PersonalCuttingInput(CuttingPass Context,string ConditionKey,double Rpm,double FeedMmMin,
    string Source,DateOnly VerifiedOn,bool Confirmed);

public partial class ComponentTools
{
    [McpServerTool(Name="review_cutting_conditions",ReadOnly=true),Description(
        "Match a manufacturer's article or series feeds and speeds to this pass. Specify material, operation or supplier operation code, apMm, aeMm, coolant and known limits. " +
        "For turning specify the workpiece cutting diameter for rpm conversion. All limits are supplied facts; missing conditions remain unverified. " +
        "Returns publisher ranges, conversions, checked limits and separately the caller's private tested corrections. Staff permission required.")]
    public Task<string> ReviewCuttingConditions(string id,CuttingPass context) => Replies.WithToken(tokens,async token=>{
        if(Ids.Of(id) is not { } cid)return Ids.NotAComponent(id);
        return (await api.ReviewCutting(cid,context,token)).ToJsonString();
    });

    [McpServerTool(Name="save_personal_cutting_setting"),Description(
        "Save the signed-in employee's tested fixed spindle rpm and table feed for an exact supplier condition and their machine, material, " +
        "assembly and operation. Supply a condition key from review_cutting_conditions, source and verification date. " +
        "Ask the operator to confirm the values before setting confirmed=true. The manufacturer's values remain available separately.")]
    public Task<string> SavePersonalCuttingSetting(string id,CuttingPass context,string conditionKey,double rpm,double feedMmMin,
        string source,DateOnly verifiedOn,bool confirmed=false) => Replies.WithToken(tokens,async token=>{
        if(Ids.Of(id) is not { } cid)return Ids.NotAComponent(id);
        if(!confirmed)return "ERROR: confirm the personally tested speed/feed, context, date and source.";
        return (await api.SavePersonalCutting(cid,new(context,conditionKey,rpm,feedMmMin,source,verifiedOn,true),token)).ToJsonString();
    });
}
