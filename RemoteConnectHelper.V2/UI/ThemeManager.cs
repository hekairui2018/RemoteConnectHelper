using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush=System.Windows.Media.Brush;
using Control=System.Windows.Controls.Control;
using CheckBox=System.Windows.Controls.CheckBox;
using ContextMenu=System.Windows.Controls.ContextMenu;
using MenuItem=System.Windows.Controls.MenuItem;
using Forms=System.Windows.Forms;

namespace RemoteConnectHelper.V2.UI;
public static class ThemeManager
{
    public static string Current {get;private set;}="Light";
    public static bool IsDark {get;private set;}
    public static event Action? Changed;
    private static bool _ready;
    private static readonly Dictionary<string,(string Light,string Dark)> Palette=new()
    {
        ["Theme213351"]=("#213351","#E1E8F3"),
        ["Theme243651"]=("#243651","#E1E8F3"),
        ["Theme2863D4"]=("#2863D4","#86ABFF"),
        ["Theme315FD5"]=("#315FD5","#86ABFF"),
        ["Theme33445F"]=("#33445F","#E1E8F3"),
        ["Theme364358"]=("#364358","#E1E8F3"),
        ["Theme3D5E91"]=("#3D5E91","#E1E8F3"),
        ["Theme4F5D71"]=("#4F5D71","#E1E8F3"),
        ["Theme5486E5"]=("#5486E5","#A2B0C4"),
        ["Theme65758F"]=("#65758F","#E1E8F3"),
        ["Theme71829C"]=("#71829C","#A2B0C4"),
        ["Theme748197"]=("#748197","#A2B0C4"),
        ["Theme78869D"]=("#78869D","#A2B0C4"),
        ["Theme7B8798"]=("#7B8798","#A2B0C4"),
        ["Theme7D8BA2"]=("#7D8BA2","#A2B0C4"),
        ["Theme8A98AD"]=("#8A98AD","#A2B0C4"),
        ["Theme8B97A9"]=("#8B97A9","#A2B0C4"),
        ["Theme8B98AC"]=("#8B98AC","#A2B0C4"),
        ["Theme91A0B6"]=("#91A0B6","#A2B0C4"),
        ["Theme94A0B1"]=("#94A0B1","#A2B0C4"),
        ["Theme94A0B2"]=("#94A0B2","#A2B0C4"),
        ["ThemeD3D9E2"]=("#D3D9E2","#343E4D"),
        ["ThemeD5DFEF"]=("#D5DFEF","#343E4D"),
        ["ThemeD6E0EF"]=("#D6E0EF","#343E4D"),
        ["ThemeDCE3EF"]=("#DCE3EF","#343E4D"),
        ["ThemeDFE3E9"]=("#DFE3E9","#343E4D"),
        ["ThemeE6EAF0"]=("#E6EAF0","#343E4D"),
        ["ThemeE6EBF2"]=("#E6EBF2","#343E4D"),
        ["ThemeE8EBEF"]=("#E8EBEF","#343E4D"),
        ["ThemeEAF1FF"]=("#EAF1FF","#252D39"),
        ["ThemeEDF2FA"]=("#EDF2FA","#252D39"),
        ["ThemeEEF3FC"]=("#EEF3FC","#252D39"),
        ["ThemeF0F2F5"]=("#F0F2F5","#252D39"),
        ["ThemeF1F5FC"]=("#F1F5FC","#252D39"),
        ["ThemeFCFDFF"]=("#FCFDFF","#252D39"),
        ["ThemeWhite"]=("White","#202630"),
    };
    public static string Normalize(string? value)=>value is "Dark" or "System"?value:"Light";
    public static bool SystemIsDark(){try{return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int n&&n==0;}catch{return false;}}
    public static string? KeyForBrush(System.Windows.Media.Brush brush)=>Palette.Keys.FirstOrDefault(key=>ReferenceEquals(System.Windows.Application.Current.Resources[key],brush));
    public static void Ensure(){if(!_ready)Apply("Light");}
    public static Brush Brush(string key)=>(Brush)System.Windows.Application.Current.Resources[key];
    public static void Apply(string? mode)
    {
        Current=Normalize(mode);IsDark=Current=="Dark"||Current=="System"&&SystemIsDark();
        var resources=System.Windows.Application.Current.Resources;
        foreach(var entry in Palette){var color=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(IsDark?entry.Value.Dark:entry.Value.Light);if(resources[entry.Key] is SolidColorBrush brush&&!brush.IsFrozen)brush.Color=color;else resources[entry.Key]=new SolidColorBrush(color);}
        if(!_ready){
            Style StyleFor(Type type,params (DependencyProperty Property,string Key)[] setters){var style=new Style(type);foreach(var setter in setters)style.Setters.Add(new Setter(setter.Property,new DynamicResourceExtension(setter.Key)));return style;}
            resources[typeof(Window)]=StyleFor(typeof(Window),(Control.BackgroundProperty,"ThemeWhite"),(Control.ForegroundProperty,"Theme33445F"));
            resources[typeof(TextBlock)]=StyleFor(typeof(TextBlock),(TextBlock.ForegroundProperty,"Theme33445F"));
            resources[typeof(CheckBox)]=StyleFor(typeof(CheckBox),(Control.ForegroundProperty,"Theme33445F"));
            resources[typeof(ContextMenu)]=StyleFor(typeof(ContextMenu),(Control.BackgroundProperty,"ThemeWhite"),(Control.ForegroundProperty,"Theme33445F"),(Control.BorderBrushProperty,"ThemeDCE3EF"));
            resources[typeof(MenuItem)]=StyleFor(typeof(MenuItem),(Control.ForegroundProperty,"Theme33445F"));
            _ready=true;
        }
        foreach(Window window in System.Windows.Application.Current.Windows){if(window is not ConnectionCard&&window is not FloatingBall&&window.Tag as string!="PinnedPreview")window.SetResourceReference(Control.BackgroundProperty,"ThemeWhite");}
        Changed?.Invoke();
    }
    public static Forms.ToolStripMenuItem CreateMenu(Action<string> changed)
    {
        var menu=new Forms.ToolStripMenuItem("主题");
        foreach(var item in new[]{("浅色","Light"),("深色","Dark"),("跟随系统","System")}){
            var option=new Forms.ToolStripMenuItem(item.Item1){Tag=item.Item2};option.Click+=(_,_)=>changed(item.Item2);menu.DropDownItems.Add(option);
        }
        menu.DropDownOpening+=(_,_)=>{foreach(var item in menu.DropDownItems.OfType<Forms.ToolStripMenuItem>())item.Checked=item.Tag as string == Current;};
        return menu;
    }
    public static void ApplyMenu(Forms.ToolStrip menu)
    {
        menu.Renderer=new Forms.ToolStripProfessionalRenderer(new MenuColors(IsDark));
        var background=System.Drawing.ColorTranslator.FromHtml(IsDark?"#202630":"#FFFFFF");
        var foreground=System.Drawing.ColorTranslator.FromHtml(IsDark?"#E1E8F3":"#33445F");menu.BackColor=background;menu.ForeColor=foreground;
        void Control(Forms.Control control){control.BackColor=background;control.ForeColor=foreground;foreach(Forms.Control child in control.Controls)Control(child);}
        foreach(Forms.ToolStripItem item in menu.Items){item.ForeColor=foreground;if(item is Forms.ToolStripDropDownItem drop)ApplyMenu(drop.DropDown);if(item is Forms.ToolStripControlHost host)Control(host.Control);}
    }
    private sealed class MenuColors(bool dark):Forms.ProfessionalColorTable
    {
        private System.Drawing.Color Base=>System.Drawing.ColorTranslator.FromHtml(dark?"#202630":"#FFFFFF");
        private System.Drawing.Color Selected=>System.Drawing.ColorTranslator.FromHtml(dark?"#36455C":"#EAF1FF");
        public override System.Drawing.Color ToolStripDropDownBackground=>Base;
        public override System.Drawing.Color ImageMarginGradientBegin=>Base;
        public override System.Drawing.Color ImageMarginGradientMiddle=>Base;
        public override System.Drawing.Color ImageMarginGradientEnd=>Base;
        public override System.Drawing.Color MenuItemSelected=>Selected;
        public override System.Drawing.Color MenuItemBorder=>Selected;
        public override System.Drawing.Color MenuItemSelectedGradientBegin=>Selected;
        public override System.Drawing.Color MenuItemSelectedGradientEnd=>Selected;
        public override System.Drawing.Color MenuItemPressedGradientBegin=>Selected;
        public override System.Drawing.Color MenuItemPressedGradientMiddle=>Selected;
        public override System.Drawing.Color MenuItemPressedGradientEnd=>Selected;
    }
}
