using System.Windows;
using System.Windows.Controls;
using RemoteConnectHelper.V2.Discovery;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using TextBox=System.Windows.Controls.TextBox;

namespace RemoteConnectHelper.V2.UI;
public sealed class SoftwareManagerWindow:Window
{
    private readonly SoftwareCatalog _catalog;
    private readonly StackPanel _rows=new();
    private readonly HashSet<string> _checked=[];
    private readonly Button _delete=new(){Content="删除",IsEnabled=false,MinWidth=70,Padding=new Thickness(14,6,14,6)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
    private bool _scanning;
    internal Func<string,string?> RenamePrompt {get;set;}
    internal Func<string,bool> ConfirmDelete {get;set;}
    internal Func<string?> PickExecutable {get;set;}
    public SoftwareManagerWindow(SoftwareCatalog catalog)
    {
        _catalog=catalog;Title="灰Desk · 远程软件";Width=560;Height=540;MinWidth=420;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        RenamePrompt=PromptName;ConfirmDelete=text=>System.Windows.MessageBox.Show(this,text,"删除软件条目",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
        PickExecutable=()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Title="选择软件启动程序",Filter="程序启动器|*.exe",CheckFileExists=true};return dialog.ShowDialog(this)==true?dialog.FileName:null;};
        var root=new Grid{Margin=new Thickness(20)};root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var toolbar=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,Margin=new Thickness(0,0,0,14)};
        var add=new Button{Content="添加",MinWidth=70,Padding=new Thickness(14,6,14,6),Margin=new Thickness(0,0,8,0)};
        add.Click+=(_,_)=>{var menu=new System.Windows.Controls.ContextMenu{PlacementTarget=add,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
            var automatic=new System.Windows.Controls.MenuItem{Header="自动添加",IsEnabled=!_scanning};automatic.Click+=async(_,_)=>await AutoAddAsync();
            var manual=new System.Windows.Controls.MenuItem{Header="手动添加"};manual.Click+=(_,_)=>{var path=PickExecutable();if(path!=null)Attempt(()=>_catalog.Add(path));};menu.Items.Add(automatic);menu.Items.Add(manual);add.ContextMenu=menu;menu.IsOpen=true;};
        _delete.Click+=(_,_)=>DeleteChecked();toolbar.Children.Add(add);toolbar.Children.Add(_delete);root.Children.Add(toolbar);
        var scroll=new ScrollViewer{Content=_rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);root.Children.Add(scroll);Grid.SetRow(_status,2);root.Children.Add(_status);Content=root;
        _catalog.Changed+=Refresh;Closed+=(_,_)=>_catalog.Changed-=Refresh;Refresh();
    }
    private void Attempt(Action action){try{action();_status.Text="";}catch(Exception ex){_status.Text=ex.Message;}}
    private void Refresh()
    {
        _rows.Children.Clear();_checked.RemoveWhere(id=>!_catalog.Entries.Any(e=>e.Id==id));_delete.IsEnabled=_checked.Count>0;
        foreach(var entry in _catalog.Entries){
            var row=new Grid{Margin=new Thickness(0,0,0,15)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(26)});row.ColumnDefinitions.Add(new ColumnDefinition());
            var check=new CheckBox{IsChecked=_checked.Contains(entry.Id),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,4,0,0),Tag=entry.Id};
            check.Checked+=(_,_)=>{_checked.Add(entry.Id);_delete.IsEnabled=true;};check.Unchecked+=(_,_)=>{_checked.Remove(entry.Id);_delete.IsEnabled=_checked.Count>0;};row.Children.Add(check);
            var details=new StackPanel();Grid.SetColumn(details,1);row.Children.Add(details);
            details.Children.Add(new TextBlock{Text=entry.Name,FontWeight=FontWeights.SemiBold,FontSize=14,ToolTip=entry.App==Domain.RemoteApp.Unknown?"按控件标签自适应定位；不明确时转人工":"已适配连接（需客户端支持）"});
            details.Children.Add(new TextBlock{Text=entry.Path??"自动查找启动程序",TextWrapping=TextWrapping.Wrap,FontSize=11,Opacity=.75,Margin=new Thickness(0,5,0,8)});
            var actions=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};
            var path=new Button{Content="更换路径",Padding=new Thickness(9,4,9,4),Margin=new Thickness(0,0,8,0)};
            path.Click+=(_,_)=>{var selected=PickExecutable();if(selected!=null)Attempt(()=>_catalog.ReplacePath(entry.Id,selected));};
            var rename=new Button{Content="更换名称",Padding=new Thickness(9,4,9,4)};rename.Click+=(_,_)=>{var name=RenamePrompt(entry.Name);if(name!=null)Attempt(()=>_catalog.Rename(entry.Id,name));};
            actions.Children.Add(path);actions.Children.Add(rename);details.Children.Add(actions);_rows.Children.Add(row);
        }
        if(_catalog.Entries.Count==0)_rows.Children.Add(new TextBlock{Text="暂无软件，点击添加。",Opacity=.7});
    }
    internal void DeleteChecked(){
        var entries=_catalog.Entries.Where(e=>_checked.Contains(e.Id)).ToArray();if(entries.Length==0)return;
        string message="从助手中删除以下软件条目？\n\n"+string.Join("\n",entries.Select(e=>"• "+e.Name+(e.Path==null?"":"\n  "+e.Path)))+"\n\n只移除列表条目，不删除或卸载软件。";
        if(ConfirmDelete(message))_catalog.Remove(entries.Select(e=>e.Id));
    }
    private async Task AutoAddAsync(){
        if(_scanning)return;_scanning=true;_status.Text="正在查找已安装的软件…";
        try{var found=await Task.Run(RemoteSoftwareDiscovery.Discover);int added=0,updated=0;foreach(var entry in found.Where(e=>e!=null)){var change=_catalog.MergeDiscovered(entry!);if(change=="added")added++;else if(change=="updated")updated++;}_status.Text=$"找到{found.Count(e=>e!=null)}款，新增{added}款，更新路径{updated}款";}
        catch(Exception ex){_status.Text="自动添加未完成："+ex.Message;}finally{_scanning=false;}
    }
    private string? PromptName(string original){
        var box=new TextBox{Text=original,MaxLength=40,Margin=new Thickness(0,0,0,14),Padding=new Thickness(8)};
        var confirm=new Button{Content="确定",MinWidth=80,HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Padding=new Thickness(12,5,12,5)};
        var panel=new StackPanel{Margin=new Thickness(20)};panel.Children.Add(new TextBlock{Text="软件名称",Margin=new Thickness(0,0,0,8)});panel.Children.Add(box);panel.Children.Add(confirm);
        var window=new Window{Owner=this,Title="更换名称",Width=360,Height=190,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel};
        confirm.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(box.Text)){window.DialogResult=true;}};window.Loaded+=(_,_)=>{box.Focus();box.SelectAll();};
        return window.ShowDialog()==true?box.Text:null;
    }
}
