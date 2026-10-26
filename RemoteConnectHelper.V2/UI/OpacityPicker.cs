using System.Diagnostics;
using Forms=System.Windows.Forms;
namespace RemoteConnectHelper.V2.UI;

/// <summary>Live opacity editor; holding an arrow repeats unit steps at an increasing rate.</summary>
public sealed class OpacityPicker:Forms.UserControl
{
    private readonly Forms.TrackBar _slider=new(){Orientation=Forms.Orientation.Vertical,Minimum=10,Maximum=100,TickFrequency=10,SmallChange=1,LargeChange=10,Left=8,Top=8,Width=48,Height=180};
    private readonly Forms.TextBox _number=new(){Left=62,Top=58,Width=48,TextAlign=Forms.HorizontalAlignment.Center};
    private readonly Forms.Button _up=new(){Text="▲",Left=62,Top=26,Width=48,Height=27};
    private readonly Forms.Button _down=new(){Text="▼",Left=62,Top=90,Width=48,Height=27};
    private readonly Forms.Timer _repeat=new();
    private readonly Stopwatch _held=new();
    private bool _sync;private int _direction;
    public int Value{get;private set;}=85;
    public event Action<int>? ValueChanged;
    public OpacityPicker()
    {
        Size=new System.Drawing.Size(126,200);BackColor=System.Drawing.Color.White;
        Controls.AddRange([_slider,_number,_up,_down,new Forms.Label{Text="%",Left=111,Top=61,Width=14}]);
        _slider.Value=85;_number.Text="85";
        _slider.ValueChanged+=(_,_)=>SetValue(_slider.Value);
        _number.TextChanged+=(_,_)=>{if(!_sync&&int.TryParse(_number.Text,out int n)&&n>=10&&n<=100)Apply(n,false);};
        _number.Leave+=(_,_)=>CommitText();
        _number.KeyDown+=(_,e)=>{if(e.KeyCode==Forms.Keys.Enter){CommitText();e.SuppressKeyPress=true;}else if(e.KeyCode==Forms.Keys.Up||e.KeyCode==Forms.Keys.Down){SetValue(Value+(e.KeyCode==Forms.Keys.Up?1:-1));e.SuppressKeyPress=true;}};
        foreach(var pair in new[]{(_up,1),(_down,-1)})
        {
            var (button,direction)=pair;
            button.MouseDown+=(_,e)=>{if(e.Button==Forms.MouseButtons.Left)BeginStep(direction);};
            button.MouseUp+=(_,_)=>EndStep();button.MouseCaptureChanged+=(_,_)=>{if(!button.Capture)EndStep();};
            button.KeyDown+=(_,e)=>{if(e.KeyCode==Forms.Keys.Space||e.KeyCode==Forms.Keys.Enter){SetValue(Value+direction);e.SuppressKeyPress=true;}};
        }
        _repeat.Tick+=(_,_)=>{SetValue(Value+_direction);_repeat.Interval=RepeatInterval(_held.ElapsedMilliseconds);};
    }
    public void SetValue(int value)=>Apply(value,true);
    private void Apply(int value,bool updateText)
    {
        value=Math.Clamp(value,10,100);bool changed=Value!=value;Value=value;
        _sync=true;try{_slider.Value=value;if(updateText)_number.Text=value.ToString();}finally{_sync=false;}
        if(changed)ValueChanged?.Invoke(value);
    }
    private void CommitText()=>SetValue(int.TryParse(_number.Text,out int n)?n:Value);
    internal static int RepeatInterval(long elapsed)=>elapsed<800?180:elapsed<1600?90:elapsed<2500?45:25;
    internal void BeginStep(int direction){EndStep();_direction=Math.Sign(direction);SetValue(Value+_direction);_held.Restart();_repeat.Interval=400;_repeat.Start();}
    internal void EndStep(){_repeat.Stop();_held.Stop();}
    protected override void Dispose(bool disposing){if(disposing){EndStep();_repeat.Dispose();}base.Dispose(disposing);}
}
