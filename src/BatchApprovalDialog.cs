using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ToolsMcp;

// A top-level Windows dialog owned by the IPC helper itself. ENCY stays interactive even if the helper exits.
[SupportedOSPlatform("windows")]
internal static class BatchApprovalDialog
{
    private const int Apply = 1, Cancel = 2, Summary = 100;
    private const uint VisibleChild = 0x50000000, TabStop = 0x10000;
    private delegate IntPtr DialogProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern IntPtr DialogBoxIndirectParamW(IntPtr module, IntPtr template, IntPtr owner, DialogProcedure proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EndDialog(IntPtr window, IntPtr result);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern bool SetDlgItemTextW(IntPtr window, int id, string text);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern int GetWindowTextLengthW(IntPtr control);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll", EntryPoint="SendMessageW", ExactSpelling=true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr control);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint interval, IntPtr callback);

    internal static bool Show(string summary, int count, Func<bool> disconnected)
    {
        var template = Template(count);
        var memory = GCHandle.Alloc(template, GCHandleType.Pinned);
        try
        {
            IntPtr Handle(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
            {
                switch (message)
                {
                    case 0x0110: // WM_INITDIALOG
                        SendMessage(GetDlgItem(window, Summary), 0x00C5, (IntPtr)summary.Length, IntPtr.Zero); // EM_SETLIMITTEXT
                        if (!SetDlgItemTextW(window, Summary, summary) ||
                            GetWindowTextLengthW(GetDlgItem(window, Summary)) != summary.Length ||
                            SetTimer(window, new UIntPtr(1), 300, IntPtr.Zero) == UIntPtr.Zero) {
                            EndDialog(window, (IntPtr)Cancel);
                            return (IntPtr)1;
                        }
                        SendMessage(window, 0x0401, (IntPtr)Cancel, IntPtr.Zero); // DM_SETDEFID
                        SetFocus(GetDlgItem(window, Cancel));
                        return IntPtr.Zero;
                    case 0x0111: // WM_COMMAND
                        int id = (int)(wParam.ToUInt64() & 0xffff);
                        if (id != Apply && id != Cancel) return IntPtr.Zero;
                        EndDialog(window, (IntPtr)(id == Apply && !disconnected() ? Apply : Cancel));
                        return (IntPtr)1;
                    case 0x0113 when disconnected(): // WM_TIMER
                    case 0x0010: // WM_CLOSE
                        EndDialog(window, (IntPtr)Cancel);
                        return (IntPtr)1;
                    default: return IntPtr.Zero;
                }
            }
            return DialogBoxIndirectParamW(IntPtr.Zero, memory.AddrOfPinnedObject(), IntPtr.Zero, Handle, IntPtr.Zero) == (IntPtr)Apply;
        }
        finally { memory.Free(); }
    }

    // Standard DLGTEMPLATE, with a scrollable read-only edit and No as the default push button.
    private static byte[] Template(int count)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen:true);
        void Word(int n) => writer.Write((ushort)n);
        void Text(string s) => writer.Write(Encoding.Unicode.GetBytes(s + "\0"));
        void Rect(int x,int y,int w,int h) { Word(x); Word(y); Word(w); Word(h); }
        void Control(int id,int kind,string caption,uint style,int x,int y,int w,int h)
        {
            while (stream.Position % 4 != 0) writer.Write((byte)0);
            writer.Write(VisibleChild | TabStop | style); writer.Write(0u);
            Rect(x,y,w,h); Word(id); Word(0xffff); Word(kind); Text(caption); Word(0);
        }
        writer.Write(0x80C808C0u); // popup, caption, system menu, DS_CENTER, DS_MODALFRAME, DS_SETFONT
        writer.Write(0u); Word(3); Rect(0,0,360,215); Word(0); Word(0);
        Text($"ENCY · Подтверждение плана ({count})"); Word(9); Text("Segoe UI");
        Control(Summary,0x81,"",0x00A00844,7,7,346,176); // border, vertical scroll, multiline, read-only, autovscroll
        Control(Apply,0x80,"Применить план",0,190,190,87,18);
        Control(Cancel,0x80,"Отмена",1,282,190,71,18);
        return stream.ToArray();
    }
}
