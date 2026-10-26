using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Parsing;

/// <summary>Display/fill policy only. Original OCR evidence and connection decisions are preserved.</summary>
public static class PreviewCandidates
{
    public sealed record Choice(DeviceCodeCandidate Code,IReadOnlyList<PasswordCandidate> Passwords);
    public static IReadOnlyList<Choice> Codes(RecognitionResult result)
    {
        var sets=new[]{(result.DeviceCodes,result.Passwords)}.Concat(result.Attempts.Select(a=>(a.DeviceCodes,a.Passwords)));
        var all=sets.SelectMany(s=>s.Item1.Select(c=>new Choice(c,s.Item2))).ToArray();
        double best=result.SelectedCode?.RankingScore??all.Select(c=>c.Code.RankingScore).DefaultIfEmpty(0).Max();
        bool Labelled(DeviceCodeCandidate c)=>c.Evidence.Any(e=>e.Contains("标签邻近")||e.Contains("本机区域"));
        var credible=all.Where(c=>ReferenceEquals(c.Code,result.SelectedCode)||
            ConnectionPolicy.ValidCode(c.Code.App,c.Code.Value)&&!c.Code.HadCharacterConversion&&
            (Labelled(c.Code)||c.Code.RankingScore>=best-ConfidenceEngine.CandidateMargin))
            .GroupBy(c=>(c.Code.App,c.Code.Value)).Select(g=>g.OrderByDescending(c=>c.Code.RankingScore).First()).ToArray();
        // Confidence bands precede software preference; Sunlogin wins only close alternatives.
        return credible.OrderByDescending(c=>Math.Floor(c.Code.RankingScore/ConfidenceEngine.CandidateMargin))
            .ThenBy(c=>c.Code.App==RemoteApp.Sunlogin?0:c.Code.App==RemoteApp.ToDesk?1:2)
            .ThenByDescending(c=>c.Code.RankingScore).ToArray();
    }
    public static IReadOnlyList<PasswordCandidate> Passwords(Choice choice)
    {
        var all=choice.Passwords.Where(p=>p.DeviceCandidateId==choice.Code.Id).ToArray();
        double best=all.Select(p=>p.RuleScore).DefaultIfEmpty(0).Max();
        return all.Where(p=>p.RuleScore>=best-ConfidenceEngine.CandidateMargin||p.Evidence.Any(e=>e.Contains("密码标签邻近")))
            .OrderByDescending(p=>p.RuleScore).GroupBy(p=>p.Value).Select(g=>g.First()).ToArray();
    }
    public static RecognitionResult ForFill(RecognitionResult result)
    {
        var choices=Codes(result);var preferred=choices.FirstOrDefault();if(preferred==null)return result;
        var passwords=Passwords(preferred);bool multiple=choices.Count>1||passwords.Count>1;
        return result with{SelectedCode=preferred.Code,SelectedPassword=passwords.FirstOrDefault(),
            Decision=multiple?ConnectionDecision.Manual:result.Decision,
            Reason=multiple?"存在多组有效候选，已填入首选，请核对后连接":result.Reason};
    }
}
