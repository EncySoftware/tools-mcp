using System.Reflection;

namespace ToolsMcp;

internal static partial class EncyIpc
{
    private static string ResolveOperationUnits(Handle tech, Handle operation, string units, bool verifySelection = true)
    {
        if(units is "metric" or "imperial")return units;
        if(units!="auto")throw new ArgumentException("Specify auto, metric or imperial.");
        if(!Guid.TryParse(operation.Text("GetId"),out var id))throw new InvalidOperationException("The operation has no stable ID. Specify its displayed units.");
        var folder=Directory.CreateTempSubdirectory("ency-operation-units-");
        string file=Path.Combine(folder.FullName,"selected.eop");
        try {
            tech.Call("ExportOperation",[operation.Text("GetId"),file]);
            string? answer=EopUnits.Read(file,id);
            if(answer==null)throw new InvalidDataException("The exported operation does not declare recognized units.");
            if(verifySelection && (!Guid.TryParse(tech.Object("get_CurrentOperation").Text("GetId"),out var current) || current!=id))
                throw new InvalidOperationException("Selected operation changed during export. Repeat the search.");
            return answer;
        } catch(Exception e) when (e is IOException or InvalidDataException or NotSupportedException or TargetInvocationException or
                                      UnauthorizedAccessException or System.Xml.XmlException) {
            throw new InvalidOperationException("Could not verify units for the selected operation. Specify metric or imperial as displayed by ENCY.");
        } finally {
            if(folder.Exists)folder.Delete(true);
        }
    }
}
