using System.Diagnostics;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;

namespace RemoteConnectHelper.V2.Discovery;
public sealed record SoftwareEntry(string Id,string Name,string? Path,string? ConnectorId)
{
    public RemoteApp App=>ConnectorId switch{"sunlogin"=>RemoteApp.Sunlogin,"todesk"=>RemoteApp.ToDesk,"anydesk"=>RemoteApp.AnyDesk,"uu"=>RemoteApp.UU,"rustdesk"=>RemoteApp.RustDesk,"teamviewer"=>RemoteApp.TeamViewer,"ultraviewer"=>RemoteApp.UltraViewer,_=>RemoteApp.Unknown};
}
public sealed class SoftwareCatalog
{
    private readonly Preferences _preferences;
    private readonly Action _save;
    public event Action? Changed;
    public IReadOnlyList<SoftwareEntry> Entries=>_preferences.SoftwareEntries!;
    public SoftwareCatalog(Preferences preferences,Action? save=null){
        _preferences=preferences;_save=save??(()=>{});
        preferences.SoftwareEntries??=[new("sunlogin","向日葵",null,"sunlogin"),new("todesk","ToDesk",null,"todesk")];
    }
    private void Commit(){_save();Changed?.Invoke();}
    public bool Contains(RemoteApp app)=>Entries.Any(e=>e.App==app&&app!=RemoteApp.Unknown);
    public static bool ValidExecutable(string path){
        try{
            if(!System.IO.Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))return false;
            using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);
            if(stream.Length<256||reader.ReadUInt16()!=0x5a4d)return false;stream.Position=0x3c;int pe=reader.ReadInt32();
            if(pe<64||pe>stream.Length-6)return false;stream.Position=pe;if(reader.ReadUInt32()!=0x4550)return false;
            if(reader.ReadUInt16() is not (0x14c or 0x8664 or 0xaa64))return false;
            var version=FileVersionInfo.GetVersionInfo(path);
            return !System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(path)+" "+version.FileDescription,@"setup|installer|uninstall|安装程序",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }catch{return false;}
    }
    public SoftwareEntry Add(string path,bool validate=true){
        path=System.IO.Path.GetFullPath(path);if(validate&&!ValidExecutable(path))throw new InvalidDataException("请选择软件启动程序（.exe），不要选择安装或卸载程序");
        var existing=Entries.FirstOrDefault(e=>string.Equals(e.Path,path,StringComparison.OrdinalIgnoreCase));if(existing!=null)return existing;
        string? connector=validate?RemoteSoftwareDiscovery.Definitions.FirstOrDefault(d=>d.CanConnect&&RemoteSoftwareDiscovery.Validate(d,path))?.Id:null;
        // A known built-in entry is updated in place, preserving any user supplied alias.
        var builtin=connector==null?null:Entries.FirstOrDefault(e=>e.ConnectorId==connector&&e.Path==null);
        var entry=builtin!=null?builtin with{Path=path,Name=System.IO.Path.GetFileNameWithoutExtension(path)}:new SoftwareEntry(connector!=null&&!Entries.Any(e=>e.Id==connector)?connector:Guid.NewGuid().ToString("N"),System.IO.Path.GetFileNameWithoutExtension(path),path,connector);
        var list=_preferences.SoftwareEntries!;if(builtin!=null)list[list.IndexOf(builtin)]=entry;else list.Add(entry);Commit();return entry;
    }
    public string MergeDiscovered(InstalledSoftware software)
    {
        var definition=RemoteSoftwareDiscovery.Definitions.SingleOrDefault(d=>d.Id==software.Id);
        if(definition==null||!RemoteSoftwareDiscovery.Validate(definition,software.Path))throw new InvalidDataException("发现的软件未通过身份校验");
        return MergeVerified(software);
    }
    internal string MergeVerified(InstalledSoftware software)
    {
        var list=_preferences.SoftwareEntries!;
        int index=list.FindIndex(e=>e.ConnectorId==software.Id||string.Equals(e.Path,software.Path,StringComparison.OrdinalIgnoreCase));
        if(index>=0){
            var entry=list[index];
            if(string.Equals(entry.Path,software.Path,StringComparison.OrdinalIgnoreCase)&&entry.ConnectorId==software.Id)return "unchanged";
            list[index]=entry with{Path=software.Path,ConnectorId=software.Id};Commit();return "updated";
        }
        list.Add(new SoftwareEntry(list.Any(e=>e.Id==software.Id)?Guid.NewGuid().ToString("N"):software.Id,System.IO.Path.GetFileNameWithoutExtension(software.Path),software.Path,software.Id));
        Commit();return "added";
    }
    public void Rename(string id,string name){
        name=name.Trim();if(name.Length is <1 or >40||name.Any(char.IsControl))throw new InvalidDataException("名称请输入1至40个字符");
        int index=_preferences.SoftwareEntries!.FindIndex(e=>e.Id==id);if(index<0)return;
        _preferences.SoftwareEntries[index]=Entries[index] with{Name=name};Commit();
    }
    public void ReplacePath(string id,string path){
        int index=_preferences.SoftwareEntries!.FindIndex(e=>e.Id==id);if(index<0)return;
        var entry=Entries[index];path=System.IO.Path.GetFullPath(path);
        if(!ValidExecutable(path))throw new InvalidDataException("请选择有效的软件启动程序");
        if(entry.ConnectorId!=null){var definition=RemoteSoftwareDiscovery.Definitions.Single(d=>d.Id==entry.ConnectorId);if(!RemoteSoftwareDiscovery.Validate(definition,path))throw new InvalidDataException("这个程序与当前软件不匹配，请使用添加功能");}
        _preferences.SoftwareEntries[index]=entry with{Path=path};Commit();
    }
    public void Remove(IEnumerable<string> ids){var set=ids.ToHashSet();_preferences.SoftwareEntries!.RemoveAll(e=>set.Contains(e.Id));Commit();}
}
