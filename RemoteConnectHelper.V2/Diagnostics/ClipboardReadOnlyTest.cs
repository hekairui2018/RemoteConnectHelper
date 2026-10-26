using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteConnectHelper.V2.Infrastructure;
namespace RemoteConnectHelper.V2.Diagnostics;
public static class ClipboardReadOnlyTest
{
    // Reads only. Never replaces the user's clipboard in order to construct a test fixture.
    public static int Run(string output)
    {
        uint before=ClipboardReader.GetClipboardSequenceNumber();var clock=System.Diagnostics.Stopwatch.StartNew();
        var snap=ClipboardReader.ReadAsync(()=>true,CancellationToken.None).GetAwaiter().GetResult();clock.Stop();
        uint after=ClipboardReader.GetClipboardSequenceNumber();
        var text=ClipboardReader.ReadTextOnce();uint end=ClipboardReader.GetClipboardSequenceNumber();
        bool stable=before==after&&after==end;
        File.WriteAllText(output,JsonSerializer.Serialize(new{Before=before,After=after,End=end,Unchanged=stable,ReadMs=clock.ElapsedMilliseconds,
            ImageAvailable=snap?.Image!=null,TextAvailable=text!=null,ReadSucceeded=snap!=null,
            Scope="Read-only current clipboard observation. No content or credential is recorded. Native paste and concurrent application behavior require interactive verification."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(stable?"CLIPBOARD sequence unchanged; native paste not verified":"CLIPBOARD changed externally or unavailable; cannot conclude");
        return stable?0:1;
    }
}
