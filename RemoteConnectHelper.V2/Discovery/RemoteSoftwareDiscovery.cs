using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RemoteConnectHelper.V2.Infrastructure;
namespace RemoteConnectHelper.V2.Discovery;

public sealed record SoftwareDefinition(string Id,string Name,string[] ExeNames,string[] Identity,bool CanConnect);
public sealed record InstalledSoftware(string Id,string Name,string Path,string Source,bool CanConnect);
public static class RemoteSoftwareDiscovery
{
    public static readonly SoftwareDefinition[] Definitions=[
        new("sunlogin","向日葵",["AweSun.exe","SunloginClient.exe"],["awesun","sunlogin","oray","贝锐","向日葵"],true),
        new("todesk","ToDesk",["ToDesk.exe"],["todesk","有趣科技"],true),
        new("anydesk","AnyDesk",["AnyDesk.exe"],["anydesk"],true),
        new("uu","UU远程",["GameViewer.exe"],["gameviewer","网易","netease","uu远程"],true),
        new("rustdesk","RustDesk",["rustdesk.exe"],["rustdesk","purslane"],true),
        new("teamviewer","TeamViewer",["TeamViewer.exe"],["teamviewer"],true),
        new("ultraviewer","UltraViewer",["UltraViewer_Desktop.exe","UltraViewer.exe"],["ultraviewer","duc fabulous"],true)];
    private static readonly object Gate=new();
    private static readonly Dictionary<string,InstalledSoftware> Memory=new();
    private static string Cache=>System.IO.Path.Combine(Log.Root,"software-paths.json");
    public static bool IdentityMatches(SoftwareDefinition d,string filename,string metadata,bool portable=false)
        => d.ExeNames.Contains(filename,StringComparer.OrdinalIgnoreCase)
        && !Regex.IsMatch(metadata,@"installer|setup|uninstall|bootstrap|安装程序",RegexOptions.IgnoreCase)
        && d.Identity.Any(term=>term.Any(c=>c>127)?metadata.Contains(term,StringComparison.OrdinalIgnoreCase)
            :Regex.IsMatch(metadata,@"(?<![a-z])"+Regex.Escape(term)+@"(?![a-z])",RegexOptions.IgnoreCase));
    public static bool Validate(SoftwareDefinition d,string path)
    {
        try
        {
            path=System.IO.Path.GetFullPath(path.Trim('"',' '));
            if(path.Contains(@"\Windows\Installer\",StringComparison.OrdinalIgnoreCase)||!File.Exists(path))return false;
            using(var f=File.OpenRead(path))using(var b=new BinaryReader(f))
            {
                if(f.Length<256||b.ReadUInt16()!=0x5A4D)return false;
                f.Position=0x3c;int pe=b.ReadInt32();if(pe<64||pe>f.Length-6)return false;
                f.Position=pe;if(b.ReadUInt32()!=0x4550)return false;
                ushort machine=b.ReadUInt16();if(machine is not (0x14c or 0x8664 or 0xAA64))return false;
            }
            var v=FileVersionInfo.GetVersionInfo(path);
            return IdentityMatches(d,System.IO.Path.GetFileName(path),$"{v.ProductName} {v.CompanyName} {v.FileDescription} {v.OriginalFilename}");
        }catch{return false;}
    }
    public static void Invalidate(string id){lock(Gate){Memory.Remove(id);try{var paths=ReadCache();paths.Remove(id);Save(paths);}catch{}}}
    private static Dictionary<string,string> ReadCache(){try{return JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Cache))??new();}catch{return new();}}
    private static void Save(Dictionary<string,string> paths){Directory.CreateDirectory(Log.Root);File.WriteAllText(Cache,JsonSerializer.Serialize(paths));}
    public static bool AddManual(string id,string path)
    {
        var d=Definitions.Single(x=>x.Id==id);if(!Validate(d,path))return false;
        lock(Gate){var paths=ReadCache();paths[id]=System.IO.Path.GetFullPath(path);Save(paths);Memory.Remove(id);}return true;
    }
    public static InstalledSoftware? Find(string id,bool refresh=false)
    {
        lock(Gate)
        {
            var d=Definitions.Single(x=>x.Id==id);
            var configured=Preferences.Load().SoftwareEntries?.FirstOrDefault(e=>e.ConnectorId==id&&e.Path!=null);
            if(configured?.Path is {} configuredPath&&Validate(d,configuredPath))return Remember(d,configuredPath,"用户配置");
            // Running clients precede discovery caches when no user override exists.
            foreach(var exe in d.ExeNames)
            foreach(var proc in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exe)))
            {using(proc)try{string? path=proc.MainModule?.FileName;if(path!=null&&Validate(d,path))return Remember(d,path,"已运行进程");}catch{}}
            if(!refresh && Memory.TryGetValue(id,out var found)&&Validate(d,found.Path))return found;
            var cached=ReadCache();
            if(cached.TryGetValue(id,out var stored)&&Validate(d,stored))return Remember(d,stored,"已验证缓存");
            foreach(var path in RegistryPaths(d).Concat(CommonPaths(d)).Concat(ShortcutPaths(d)))
                if(Validate(d,path))return Remember(d,path,"注册表/常用目录/快捷方式");
            return null;
        }
    }
    private static InstalledSoftware Remember(SoftwareDefinition d,string path,string source)
    {
        var value=new InstalledSoftware(d.Id,d.Name,path,source,d.CanConnect);Memory[d.Id]=value;
        try{var paths=ReadCache();paths[d.Id]=path;Save(paths);}catch{}return value;
    }
    public static InstalledSoftware[] DiscoverRunning()
    {
        var found=new Dictionary<string,InstalledSoftware>();
        foreach(var definition in Definitions)
        foreach(var executable in definition.ExeNames)
        foreach(var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(executable))){
            using(process)try{
                var path=process.MainModule?.FileName;
                if(path!=null&&Validate(definition,path)&&!found.ContainsKey(definition.Id)){
                    lock(Gate)found[definition.Id]=Remember(definition,path,"已运行进程");
                }
            }catch{}
        }
        return found.Values.ToArray();
    }
    public static InstalledSoftware?[] Discover()=>Definitions.Select(d=>Find(d.Id)).ToArray();
    private static IEnumerable<string> RegistryPaths(SoftwareDefinition d)
    {
        var paths=new List<string>();
        foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})try
        {
            using var root=RegistryKey.OpenBaseKey(hive,view);
            foreach(var exe in d.ExeNames){using var key=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+exe);if(key?.GetValue("") is string path)paths.Add(path.Trim('"'));}
            using var un=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if(un==null)continue;
            foreach(string name in un.GetSubKeyNames())try
            {
                using var key=un.OpenSubKey(name);string display=key?.GetValue("DisplayName") as string??"";
                if(!d.Identity.Any(t=>Regex.IsMatch(display,@"(?<![a-z])"+Regex.Escape(t)+@"(?![a-z])",RegexOptions.IgnoreCase)))continue;
                string icon=key?.GetValue("DisplayIcon") as string??"";
                var match=Regex.Match(icon,@"^\s*(?:""([^""]+\.exe)""|(.+?\.exe))(?:,[-\d]+)?$",RegexOptions.IgnoreCase);
                if(match.Success)paths.Add(match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value);
                string loc=key?.GetValue("InstallLocation") as string??"";
                if(loc.Length>0)paths.AddRange(d.ExeNames.Select(e=>System.IO.Path.Combine(loc.Trim('"'),e)));
            }catch{}
        }catch{}
        return paths;
    }
    private static IEnumerable<string> CommonPaths(SoftwareDefinition d)
    {
        var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}
            .Concat(DriveInfo.GetDrives().Where(x=>x.DriveType==DriveType.Fixed).Select(x=>x.RootDirectory.FullName));
        string[] dirs=d.Id switch{"sunlogin"=>["AweSun","AweSun/agent","Oray/SunLogin/SunloginClient","Program Files/Oray/SunLogin/SunloginClient"],
            "todesk"=>["ToDesk","Program Files/ToDesk"],"anydesk"=>["AnyDesk","Program Files/AnyDesk"],"uu"=>["网易UU/GameViewer","UU远程","GameViewer","Programs/GameViewer"],"rustdesk"=>["RustDesk","Tools/RustDesk","Program Files/RustDesk"],"ultraviewer"=>["UltraViewer","Program Files/UltraViewer","Program Files (x86)/UltraViewer"],_=>["TeamViewer","Program Files/TeamViewer"]};
        return roots.SelectMany(root=>dirs.SelectMany(dir=>d.ExeNames.Select(e=>System.IO.Path.Combine(root,dir,e))));
    }
    private static IEnumerable<string> ShortcutPaths(SoftwareDefinition d)
    {
        var paths=new List<string>();object? shell=null;
        try
        {
            var type=Type.GetTypeFromProgID("WScript.Shell");if(type==null)return paths;shell=Activator.CreateInstance(type);if(shell==null)return paths;
            foreach(var folder in new[]{Environment.SpecialFolder.StartMenu,Environment.SpecialFolder.CommonStartMenu,Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.CommonDesktopDirectory})
            {
                var root=Environment.GetFolderPath(folder);if(!Directory.Exists(root))continue;
                foreach(var file in Directory.EnumerateFiles(root,"*.lnk",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,MaxRecursionDepth=4}))
                {
                    if(!d.Identity.Any(t=>System.IO.Path.GetFileName(file).Contains(t,StringComparison.OrdinalIgnoreCase)))continue;
                    object? link=null;try{dynamic sh=shell;link=sh.CreateShortcut(file);dynamic l=link;paths.Add((string)l.TargetPath);}catch{}finally{if(link!=null&&Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);}
                }
            }
        }catch{}finally{if(shell!=null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
        return paths;
    }
}
