using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;

namespace ToolsMcp;

// The installed CAMIPC interop assemblies describe the ABI of that ENCY release. No ENCY binaries are redistributed.
[SupportedOSPlatform("windows")]
internal static partial class EncyIpc
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr CreateHelper();

    internal static int Execute(string[] args)
    {
        try
        {
            if (args.Length < 2 || !int.TryParse(args[1], out int pid) ||
                !(args is ["list", _] or ["import", _, _, _, _] or ["operations", _, _, _] or ["cutting", _, _, _, _, _, _, _, _] or ["operation-context", _, _] or ["drill-context", _, _] or ["drill-assign", _, _, _, _] or ["mill-assign", _, _, _, _] or ["mill-context", _, _] or ["machine-fit", _, _, _, _, _, _, _] or ["machine-assign", _, _, _, _, _, _, _, _, _, _, _]))
                throw new ArgumentException("Invalid local ENCY command.");
            using var process = Process.GetProcessById(pid);
            using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            int length = 32768;
            var name = new StringBuilder(length);
            if (handle.IsInvalid || !QueryFullProcessImageNameW(handle, 0, name, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the ENCY process path. Run MCP under the same Windows account as ENCY.");
            var exe = name.ToString();
            var bin = Path.GetDirectoryName(exe)!;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ENCY Software");
            if (!string.Equals(Path.GetFileName(exe), "ency.exe", StringComparison.OrdinalIgnoreCase)
                || !bin.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(bin), "Bin64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select a running ENCY installation in Program Files/ENCY Software.");
            var file = Path.Combine(bin, "CAMIPC.Helper.Cam.dll");
            var lib = LoadLibraryExW(file, IntPtr.Zero, 0x100 | 0x1000);
            if (lib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot load {file}.");
            // This process only serves one call. Leave the native module mapped through final COM cleanup.
            var assemblies = new AssemblyLoadContext("Installed ENCY interop");
            assemblies.Resolving += (context, name) =>
            {
                var dll = Path.Combine(bin, name.Name + ".dll");
                return File.Exists(dll) ? context.LoadFromAssemblyPath(dll) : null;
            };
            var type = assemblies.LoadFromAssemblyPath(Path.Combine(bin, "CAMIPC.Helper.dll")).GetType("CAMIPC.Helper.IIpcHelper", true)!;
            var create = Marshal.GetDelegateForFunctionPointer<CreateHelper>(NativeLibrary.GetExport(lib, "CreateHelper"));
            var ptr = create();
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("ENCY IPC helper unavailable.");
            var helper = new Handle(Marshal.GetObjectForIUnknown(ptr), type);
            var app = helper.Object("GetRunningCamApp", pid);
            var project = app.Object("GetActiveProject");
            var path = project.Text("GetFilePath");
            if (args[0] == "operation-context") {
                var selected = project.Object("GetTechnologist").Object("get_CurrentOperation").Object("get_XMLProp");
                if (SuitableCycle(selected, true)) args[0] = "mill-context";
                else if (SuitableCycle(selected, false)) args[0] = "drill-context";
                else throw new InvalidOperationException("Tool selection supports drilling, slot and shoulder milling. The selected cycle is unsupported.");
            }
            if(args[0] is "machine-fit" or "machine-assign" or "mill-context")
                return MachineCommand(args,project,assemblies,path);
            if (args[0] is "drill-context" or "drill-assign" or "mill-assign")
            {
                var tech = project.Object("GetTechnologist");
                var op = tech.Object("get_CurrentOperation");
                var id = op.Text("GetId");
                var xml = op.Object("get_XMLProp");
                if (!SuitableCycle(xml,args[0]=="mill-assign"))
                    throw new InvalidOperationException(args[0]=="mill-assign" ? "Select a supported milling operation." : "Select a drilling operation first.");
                if (args[0] is "drill-assign" or "mill-assign")
                {
                    if (string.IsNullOrWhiteSpace(path) || !string.Equals(path,args[2],StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(id,args[3],StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The selected project or operation changed.");
                    var fileName = Path.GetFullPath(args[4]);
                    if (!File.Exists(fileName) || !Path.GetExtension(fileName).Equals(".sc12tool",StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Missing tool exchange file.");
                    var listType = app.Type.GetMethod("ImportToolsFromFile")?.GetParameters()[0].ParameterType
                        ?? throw new NotSupportedException("Update ENCY to assign a tool from a file.");
                    var files = DispatchProxy.Create(listType, typeof(ToolFileList));
                    ((ToolFileList)files).FileName = fileName;
                    var imported = app.Strings("ImportToolsFromFile",files,id);
                    if (imported.Count != 1 || string.IsNullOrWhiteSpace(imported[0])) throw new InvalidOperationException("ENCY did not report a single assigned tool.");
                    var assigned = op.Object("get_XMLProp").Object("GetArray", "ToolSection.Tools").Object("get_Itm", 0);
                    if (!string.Equals(assigned.Text("GetCalculatedString", "ToolID"), imported[0], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Tool {imported[0]} was imported, but ENCY did not confirm the assignment to {id}.");
                    Console.WriteLine($"Assigned project tool {imported[0]} to {op.Text("GetName")} [{id}].");
                    return 0;
                }
                if (args[2] is not ("metric" or "imperial")) throw new ArgumentException("Specify metric or imperial.");
                var scale = args[2] == "imperial" ? 25.4 : 1;
                var job=op.Object("get_ModelFormerJobAssignment");
                var holeType=assemblies.LoadFromAssemblyName(new AssemblyName("CAMIPC.ModelFormerTypes")).GetType("CAMIPC.ModelFormerTypes.ICamIpcHoleModelItem", true)!;
                var holes=new List<object>();
                for (int i=0,count=job.Count("get_Count"); i<count; i++)
                {
                    var item=job.Object("get_Item",i);
                    if (!holeType.IsInstanceOfType(item.Value)) { holes.Add(new { unresolved = true }); continue; }
                    var hole=new Handle(item.Value,holeType);
                    var diameter=Convert.ToDouble(hole.Call("get_Diameter",[]).Value)*scale;
                    var depth=(Convert.ToDouble(hole.Call("get_TopLevel",[]).Value)-Convert.ToDouble(hole.Call("get_BottomLevel",[]).Value))*scale;
                    bool resolved=hole.Call("get_TopLevelMode",[]).Value?.ToString()!="htlmDefault" &&
                        hole.Call("get_BottomLevelMode",[]).Value?.ToString()!="hblmDefault";
                    var geometry = FeatureGeometry(project,assemblies,item.Text("get_Caption"),diameter,scale,false);
                    string? bottom=geometry?.Kind;
                    if(bottom=="blind") bottom=hole.Call("get_DrillTipCompensation",[]).Value?.ToString() switch
                    { "hdtNone"=>"tip","hdtToolTip"=>"cylinder",_=>"blind" };
                    holes.Add(new {bottom,diameterMm=double.IsFinite(diameter)&&diameter>0?(double?)diameter:null,
                        depthMm=resolved && double.IsFinite(depth)&&depth>0?(double?)depth:null});
                }
                const string mat="ConditionsSection.Workpiece.Material";
                var material = xml.Count("get_PropExists",mat)==0 ? null : xml.Text("GetString",mat);
                var activeTools = xml.Object("GetArray", "ToolSection.Tools");
                string? toolId = activeTools.Count("get_TopItem") < 0 ? null :
                    activeTools.Object("get_Itm", 0).Text("GetCalculatedString", "ToolID");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {processId=pid,projectPath=path,operationId=id,toolId,
                    operationName=op.Text("GetName"),operationUnits=args[2],material,holes}));
                return 0;
            }
            if (args[0] == "cutting")
            {
                if (string.IsNullOrWhiteSpace(path) || !string.Equals(path, args[2], StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(args[3]) || args[4] is not ("metric" or "imperial"))
                    throw new ArgumentException("Verify the project path, operation id and measurement system.");
                var operation = project.Object("GetTechnologist").Object("GetOperationById", args[3]);
                if (!string.Equals(operation.Text("GetId"), args[3], StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Operation id changed.");
                var xml = operation.Object("get_XMLProp");
                bool Exists(string key) => Convert.ToBoolean(xml.Call("get_PropExists", [key]).Value);
                var settings = new List<CuttingSetting>();
                for (int i = 5; i < args.Length; i += 2)
                {
                    double value = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                    settings.Add(new(args[i], value));
                }
                CuttingSetting.Apply(settings, Exists("ConditionsSection.Spindle.Speed.Mode"), args[4] == "imperial",
                    Exists, key => xml.Count("GetInteger", key), (key,v) => xml.Call("SetInteger", [key, v]),
                    key => Convert.ToDouble(xml.Call("GetFloat", [key]).Value), (key,v) => xml.Call("SetFloat", [key, v]),
                    () => operation.Call("LoadFromXmlProp", [xml.Value]));
                Console.WriteLine($"{operation.Text("GetName")} [{args[3]}]: {string.Join(", ", settings)}");
                return 0;
            }
            var tools = project.Object("get_MachineTools");
            var reply = new StringBuilder().AppendLine($"ENCY PID {pid} | Project: {path}");
            if (args[0] == "list")
            {
                var count = tools.Count("GetMagazinesCount");
                if (count == 0) reply.AppendLine("No magazines.");
                for (int i = 0; i < count; i++)
                {
                    var magazine = tools.Object("GetMagazine", i);
                    var number = magazine.Count("GetToolsCount");
                    reply.AppendLine($"{magazine.Text("GetCaption")} ({number} tools)");
                    for (int j = 0; j < number; j++)
                        reply.AppendLine("  " + Tool(magazine.Object("GetTool", j)));
                }
            }
            else if (args[0] == "operations")
            {
                if (!int.TryParse(args[3], out var position) || position < 0)
                    throw new ArgumentException("Specify a non-negative T position.");
                var tool = FindTool(tools, args[2], position);
                reply.AppendLine(Tool(tool));
                var ids = tools.Strings("GetToolOperations", tool.Count("get_NodeID"));
                reply.AppendLine($"Operations: {ids.Count}");
                if (ids.Count > 0)
                {
                    var tech = project.Object("GetTechnologist");
                    foreach (var id in ids)
                        reply.AppendLine($"{tech.Object("GetOperationById", id).Text("GetName")} [{id}]");
                }
            }
            else
            {
                if (!string.Equals(path, args[2], StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"The active project has changed. Current project: {path}");
                var zip = args[3];
                if (!Path.IsPathFullyQualified(zip) || !File.Exists(zip) || !Path.GetExtension(zip).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Pass the absolute path to a local GTC .zip file.");
                var result = tools.Object("ImportGTC", zip, "", args[4]);
                var library = result.Object("get_ComponentsLibrary");
                reply.AppendLine($"Library: {library.Text("get_Path")}");
                var n = result.Count("get_ToolsCount");
                reply.AppendLine($"Installed: {n}");
                for (int i = 0; i < n; i++) reply.AppendLine(Tool(result.Object("get_Tool", i)));
                var errors = result.Call("GetInstallErrors", []);
                var list = new Handle(errors.Value, errors.Type);
                var skipped = errors.Value == null ? 0 : list.Count("Count");
                reply.AppendLine($"Skipped: {skipped}");
                for (int i = 0; i < skipped; i++) reply.AppendLine(list.Text("Get", i));
            }
            Console.WriteLine(reply.ToString().TrimEnd());
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.GetBaseException().Message);
            return 1;
        }
    }

    private static Handle FindTool(Handle tools, string name, int position)
    {
        int magazines = tools.Count("GetMagazinesCount");
        for (int i = 0; i < magazines; i++)
        {
            var magazine = tools.Object("GetMagazine", i);
            if (!string.Equals(magazine.Text("GetName"), name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(magazine.Text("GetCaption"), name, StringComparison.OrdinalIgnoreCase)) continue;
            var count = magazine.Count("GetToolsCount");
            for (int j = 0; j < count; j++)
            {
                var node = magazine.Object("GetTool", j);
                if (node.Count("GetPosition") == position) return node;
            }
            throw new ArgumentException($"{name} T#{position} has no installed tool.");
        }
        throw new ArgumentException($"No magazine named '{name}'. Use list_project_tools.");
    }

    private static string Tool(Handle node)
    {
        var caption = node.Text("GetCaption");
        var slot = $"{node.Text("GetMagazineName")} T#{node.Count("GetPosition")}";
        var adapters = node.Strings("GetAdapters");
        return slot + " — " + string.Join(" → ", new[] { caption }.Concat(adapters));
    }

    // Implement this installation's IListString directly. The ENCY 3.4 .NET helper targets
    // .NET 10 whereas a local MCP can run on .NET 8. IPC only reads this single file list.
    public class ToolFileList : DispatchProxy
    {
        public string FileName { get; set; } = "";
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "Count" => 1,
            "Get" when args is [0] => FileName,
            "GetText" => FileName,
            "Contains" when args is [string value] => FileName == value,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    private sealed record Handle(object? Value, Type Type)
    {
        public (object? Value, Type Type) Call(string name, object?[] args)
        {
            var method = Type.GetMethod(name) ?? throw new NotSupportedException($"Installed ENCY does not expose {Type.Name}.{name}. Update ENCY to use the machine-tool API.");
            var pars = method.GetParameters();
            var contextual = pars.Length == args.Length + 1 && pars[^1].ParameterType.GetElementType()?.FullName == "CAMIPC.ExecuteContext.TExecuteContext";
            if (contextual)
            {
                var context = Activator.CreateInstance(pars[^1].ParameterType.GetElementType()!)!;
                var field = context.GetType().GetField("ExecuteSettings")!;
                var settings = field.GetValue(context)!;
                settings.GetType().GetField("Timeout")!.SetValue(settings, 120000);
                field.SetValue(context, settings);
                args = [.. args, context];
            }
            var result = method.Invoke(Value, args);
            if (contextual)
            {
                var context = args[^1]!;
                var status = context.GetType().GetField("ResultStatus")!.GetValue(context)!;
                if (status.GetType().GetField("Code")!.GetValue(status)!.ToString() == "rsError")
                    throw new InvalidOperationException($"{name}: {status.GetType().GetField("Description")!.GetValue(status)}");
            }
            return (result, method.ReturnType);
        }
        public Handle Object(string name, params object?[] args)
        {
            var (value, type) = Call(name, args);
            if (value == null) throw new InvalidOperationException(name == "get_MachineTools"
                ? "The project has no machine magazine. Open a project in advanced tools mode."
                : $"{name} returned no object.");
            return new(value, type);
        }
        public List<string> Strings(string name, params object?[] args)
        {
            var (value, type) = Call(name, args);
            var result = new List<string>();
            if (value == null) return result;
            var list = new Handle(value, type);
            int count = list.Count("Count");
            for (int i = 0; i < count; i++) result.Add(list.Text("Get", i));
            return result;
        }
        public string Text(string name, params object?[] args) => (string?)Call(name, args).Value ?? "";
        public int Count(string name, params object?[] args) => Convert.ToInt32(Call(name, args).Value);
    }
}
