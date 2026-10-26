using System.Runtime.InteropServices;
namespace RemoteConnectHelper.V2.Infrastructure;
internal static class ProcessOwnership
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Entry{
        public uint Size,Usage,PID;public UIntPtr Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;
    }
    [DllImport("kernel32.dll")]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32First(IntPtr snapshot,ref Entry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32Next(IntPtr snapshot,ref Entry entry);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    public static bool IsDescendant(int root,int child){
        if(root==child)return true;
        var snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return false;
        var parents=new Dictionary<uint,uint>();
        try{var entry=new Entry{Size=(uint)Marshal.SizeOf<Entry>(),Name=""};if(Process32First(snapshot,ref entry))do{parents[entry.PID]=entry.Parent;}while(Process32Next(snapshot,ref entry));}
        finally{CloseHandle(snapshot);}
        uint current=(uint)child;var seen=new HashSet<uint>();for(int depth=0;depth<12&&seen.Add(current)&&parents.TryGetValue(current,out uint parent);depth++){
            if(parent==(uint)root)return true;current=parent;
        }
        return false;
    }
}
