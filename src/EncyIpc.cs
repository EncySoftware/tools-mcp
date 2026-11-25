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
internal static class EncyIpc
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
                !(args is ["list", _] or ["import", _, _, _, _] or ["operations", _, _, _]))
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
