using RemoteConnectHelper.V2.Domain;
namespace RemoteConnectHelper.V2.Connections;

public sealed record ClientSessionSample(RemoteApp App,SessionSample Sample);

/// <summary>Only new sessions, or a seen connecting/verification transition, may finish this visible task.</summary>
public sealed class ManualSessionTracker
{
    private sealed class Entry { public bool Eligible; public readonly SessionEvidence Evidence=new(); }
    private readonly Dictionary<(RemoteApp,long),Entry> _windows=[];
    private bool _initialized;
    public RemoteApp? Observe(IReadOnlyList<ClientSessionSample> samples)
    {
        var current=samples.Select(s=>(s.App,s.Sample.Window)).ToHashSet();
        foreach(var key in _windows.Keys.Where(k=>!current.Contains(k)).ToArray())_windows.Remove(key);
        RemoteApp? confirmed=null;
        foreach(var item in samples)
        {
            if(!RemoteApps.Supported.Contains(item.App))continue;
            var sample=item.Sample;var key=(item.App,sample.Window);
            if(!_windows.TryGetValue(key,out var entry))_windows[key]=entry=new(){Eligible=_initialized};
            // Existing successful sessions cannot close a newly opened assistant. An observed prompt/loading
            // state allows a reused client window to prove a subsequent manually established session.
            if(sample.Verification||sample.Loading)entry.Eligible=true;
            var state=entry.Evidence.Observe(sample with{NewWindow=entry.Eligible,TargetMatches=true});
            if(state==SessionState.Confirmed)confirmed=item.App;
        }
        _initialized=true;return confirmed;
    }
    public static IReadOnlyList<ClientSessionSample> CaptureFor(RemoteApp app,string code)
    {
        var samples=new List<ClientSessionSample>();
        foreach(var window in SessionObserver.Windows(app).Take(12)){
            var sample=SessionObserver.Read(window,false,code);
            if(!sample.TargetMatches)continue;
            if(!sample.ExplicitConnected&&!sample.Failure&&!sample.Verification){
                try{
                    var state=NegativeClientStatus.ReadAsync(window,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    if(state!=null)sample=sample with{Failure=state.Status==RemoteConnectHelper.Automation.ConnectStatus.Failed,Verification=state.Status==RemoteConnectHelper.Automation.ConnectStatus.NeedPassword,Reason=state.Message};
                }catch{}
            }
            samples.Add(new(app,sample));
        }
        return samples;
    }
    public static IReadOnlyList<ClientSessionSample> Capture()
    {
        var samples=new List<ClientSessionSample>();
        foreach(var app in RemoteApps.Supported)
            foreach(var window in SessionObserver.Windows(app).Take(12))
                samples.Add(new(app,SessionObserver.Read(window,false,"")));
        return samples;
    }
}
