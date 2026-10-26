using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RemoteConnectHelper.V2.Infrastructure;
namespace RemoteConnectHelper.V2.UI;

/// <summary>Windows' native file picker, with independent folder and physical-window placement preferences.</summary>
internal static class ImageFilePicker
{
    private const int Cancelled=unchecked((int)0x800704C7);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)] private static extern void SHCreateItemFromParsingName(string path,IntPtr bind,ref Guid id,[MarshalAs(UnmanagedType.Interface)]out IShellItem item);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    private delegate void WindowEvent(IntPtr hook,uint eventId,IntPtr window,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WindowEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    public static string? Show(Window owner,Preferences preferences,CancellationToken cancellation=default,Guid? clientGuid=null)
    {
        var dialog=(IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"),true)!)!;
        var events=new DialogEvents(dialog,owner,preferences);uint cookie=0;
        try
        {
            var guid=clientGuid??new Guid("2A517062-7E57-414E-AC70-0D3543937871");dialog.SetClientGuid(ref guid);
            dialog.SetTitle("打开图片");dialog.SetOptions(0x40|0x800|0x1000|0x02000000); // filesystem, valid path/file, no shell recent-file entry
            dialog.SetFileTypes(1,[new FilterSpec{Name="图片",Spec="*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif"}]);
            if(Directory.Exists(preferences.LastImageFolder))SetFolder(dialog,preferences.LastImageFolder!);
            dialog.Advise(events,out cookie);events.Start();
            using var registration=cancellation.Register(()=>{var window=events.Window;if(window!=IntPtr.Zero)PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero);});
            int hr=dialog.Show(new WindowInteropHelper(owner).EnsureHandle());
            events.Remember();
            if(hr==Cancelled)return null;if(hr<0)Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out var result);try{return FilePath(result);}finally{Marshal.ReleaseComObject(result);}
        }
        finally{events.Dispose();if(cookie!=0)try{dialog.Unadvise(cookie);}catch{}Marshal.ReleaseComObject(dialog);}
    }
    private static string FilePath(IShellItem item)
    {item.GetDisplayName(0x80058000,out var pointer);try{return Marshal.PtrToStringUni(pointer)??"";}finally{Marshal.FreeCoTaskMem(pointer);}}
    private static void SetFolder(IFileDialog dialog,string folder)
    {var id=typeof(IShellItem).GUID;SHCreateItemFromParsingName(folder,IntPtr.Zero,ref id,out var item);try{dialog.SetFolder(item);}finally{Marshal.ReleaseComObject(item);}}
    internal static Rect Fit(Rect area,Rect current,double[]? saved)
    {
        bool restore=saved is {Length:4}&&saved.All(double.IsFinite)&&saved[2]>100&&saved[3]>100;
        double width=Math.Clamp(restore?saved![2]:current.Width,Math.Min(480,Math.Max(1,area.Width-16)),Math.Max(1,area.Width-16));
        double height=Math.Clamp(restore?saved![3]:current.Height,Math.Min(340,Math.Max(1,area.Height-16)),Math.Max(1,area.Height-16));
        return new Rect(Math.Clamp(restore?saved![0]:area.Left+(area.Width-width)/2,area.Left,area.Right-width),Math.Clamp(restore?saved![1]:area.Top+(area.Height-height)/2,area.Top,area.Bottom-height),width,height);
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    private sealed class DialogEvents:IFileDialogEvents,IDisposable
    {
        private readonly IFileDialog _dialog;private readonly Window _owner;private readonly Preferences _preferences;private readonly double[]? _saved;
        private readonly WindowEvent _callback;private IntPtr _hook,_window;private bool _placed;private Rect _last=Rect.Empty;
        public DialogEvents(IFileDialog dialog,Window owner,Preferences preferences){_dialog=dialog;_owner=owner;_preferences=preferences;_saved=preferences.ImagePickerBounds?.ToArray();_callback=OnWindowEvent;}
        public IntPtr Window=>_window;
        public void Start()=>_hook=SetWinEventHook(0x8002,0x800B,IntPtr.Zero,_callback,(uint)Environment.ProcessId,0,0);
        private void OnWindowEvent(IntPtr hook,uint id,IntPtr window,int objectId,int childId,uint thread,uint time)
        {try{if(objectId!=0||childId!=0)return;EnsurePlaced();if(window==_window){var rect=DesktopWindows.NativeBounds(window);if(!rect.IsEmpty)_last=rect;}}catch{}}
        private void EnsurePlaced()
        {
            if(_placed)return;((IOleWindow)_dialog).GetWindow(out var window);var current=DesktopWindows.NativeBounds(window);if(current.IsEmpty)return;
            _window=window;if(!IsWindowVisible(window))return;_placed=true;
            var area=_saved is {Length:4}&&_saved.All(double.IsFinite)?DesktopWindows.WorkAreaAtPoint(_saved[0]+_saved[2]/2,_saved[1]+_saved[3]/2):DesktopWindows.WorkAreaFor(_owner);
            var rect=Fit(area,current,_saved);SetWindowPos(window,IntPtr.Zero,(int)rect.X,(int)rect.Y,(int)rect.Width,(int)rect.Height,0x14);_last=rect;
        }
        public void Remember()
        {
            try{EnsurePlaced();var rect=DesktopWindows.NativeBounds(_window);if(!rect.IsEmpty)_last=rect;}catch{}
            if(!_last.IsEmpty&&_last.Width>100&&_last.Height>100)_preferences.ImagePickerBounds=[_last.X,_last.Y,_last.Width,_last.Height];
            try{_dialog.GetFolder(out var folder);try{var path=FilePath(folder);if(Directory.Exists(path))_preferences.LastImageFolder=path;}finally{Marshal.ReleaseComObject(folder);}}catch{}
        }
        public void Dispose(){Remember();if(_hook!=IntPtr.Zero)UnhookWinEvent(_hook);GC.KeepAlive(_callback);}
        public int OnFileOk(IFileDialog dialog){Remember();return 0;}
        public int OnFolderChanging(IFileDialog dialog,IShellItem folder)=>0;
        public int OnFolderChange(IFileDialog dialog){Remember();return 0;}
        public int OnSelectionChange(IFileDialog dialog){Remember();return 0;}
        public int OnShareViolation(IFileDialog dialog,IShellItem item,out uint response){response=0;return 0;}
        public int OnTypeChange(IFileDialog dialog){try{EnsurePlaced();}catch{}return 0;}
        public int OnOverwrite(IFileDialog dialog,IShellItem item,out uint response){response=0;return 0;}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct FilterSpec{[MarshalAs(UnmanagedType.LPWStr)]public string Name;[MarshalAs(UnmanagedType.LPWStr)]public string Spec;}
    [ComImport,Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]int Show(IntPtr owner);
        void SetFileTypes(uint count,[MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)]FilterSpec[] filters);
        void SetFileTypeIndex(uint index);void GetFileTypeIndex(out uint index);
        void Advise([MarshalAs(UnmanagedType.Interface)]IFileDialogEvents events,out uint cookie);void Unadvise(uint cookie);
        void SetOptions(uint options);void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);void SetFolder(IShellItem folder);void GetFolder(out IShellItem folder);void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string name);void GetFileName([MarshalAs(UnmanagedType.LPWStr)]out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string text);void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string text);
        void GetResult(out IShellItem item);void AddPlace(IShellItem item,uint location);void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)]string extension);
        void Close(int result);void SetClientGuid(ref Guid guid);void ClearClientData();void SetFilter(IntPtr filter);
    }
    [ComVisible(true),Guid("973510DB-7D7F-452B-8975-74A85828D354"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig]int OnFileOk(IFileDialog dialog);[PreserveSig]int OnFolderChanging(IFileDialog dialog,IShellItem folder);
        [PreserveSig]int OnFolderChange(IFileDialog dialog);[PreserveSig]int OnSelectionChange(IFileDialog dialog);
        [PreserveSig]int OnShareViolation(IFileDialog dialog,IShellItem item,out uint response);[PreserveSig]int OnTypeChange(IFileDialog dialog);
        [PreserveSig]int OnOverwrite(IFileDialog dialog,IShellItem item,out uint response);
    }
    [ComImport,Guid("00000114-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow{void GetWindow(out IntPtr window);void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)]bool enter);}
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context,ref Guid handler,ref Guid id,out IntPtr result);void GetParent(out IShellItem item);
        void GetDisplayName(uint kind,out IntPtr name);void GetAttributes(uint mask,out uint flags);void Compare(IShellItem item,uint hint,out int order);
    }
}
