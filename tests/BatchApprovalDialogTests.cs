using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ToolsMcp;
using Xunit;

public class BatchApprovalDialogTests
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CreateDesktopW(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private delegate bool Visitor(IntPtr window,IntPtr arg);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread,Visitor callback,IntPtr arg);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window,int id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window,StringBuilder text,int size);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window,uint msg,IntPtr w,IntPtr l);

    [Fact] public async Task IndependentWindowShowsEveryRowAndDefaultsToCancel()
    {
        if (!OperatingSystem.IsWindows()) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { if (OperatingSystem.IsWindows()) VerifyOnPrivateDesktop(); done.SetResult(); }
            catch(Exception e) { done.SetException(e); }
        }) { IsBackground=true };
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyOnPrivateDesktop()
    {
        uint id = GetCurrentThreadId();
        var previous = GetThreadDesktop(id);
        var desktop = CreateDesktopW("McpApprovalTest"+Guid.NewGuid().ToString("N"),0,0,0,0x01ff,0);
        Assert.NotEqual(IntPtr.Zero,desktop);
        try {
            Assert.True(SetThreadDesktop(desktop));
            string summary = string.Join("\r\n", Enumerable.Range(1,1500).Select(n=>$"Операция {n}: T#10 → T#72"));
            string? displayed=null; IntPtr owner=(IntPtr)(-1); int defaultButton=-1;
            bool accepted=BatchApprovalDialog.Show(summary,1500,()=>{
                EnumThreadWindows(id,(window,_)=>{
                    var edit=GetDlgItem(window,100);
                    if (edit==IntPtr.Zero) return true;
                    owner=GetWindow(window,4); // GW_OWNER
                    var text=new StringBuilder(summary.Length+1);
                    GetWindowTextW(edit,text,text.Capacity);
                    displayed=text.ToString();
                    defaultButton=(int)(SendMessageW(window,0x0400,0,0).ToInt64() & 0xffff);
                    return false;
                },0);
                return true; // Disconnect without pressing Apply.
            });
            Assert.False(accepted);
            Assert.Equal(summary,displayed);
            Assert.Equal(IntPtr.Zero,owner);
            Assert.Equal(2,defaultButton);
        } finally {
            SetThreadDesktop(previous);
            CloseDesktop(desktop);
        }
    }
}
