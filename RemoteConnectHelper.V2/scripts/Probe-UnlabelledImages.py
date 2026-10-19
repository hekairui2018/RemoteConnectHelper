"""Run the offline diagnostic entry in an isolated, bounded child per image.

Does not infer ground truth from predictions or execute remote clients.
Only the diagnostic child created here can be terminated on timeout.
"""
import argparse
import hashlib
import json
import os
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--workbook', type=Path, required=True)
    p.add_argument('--dotnet', type=Path, required=True)
    p.add_argument('--assembly', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--timeout', type=float, default=15)
    args = p.parse_args()
    if args.timeout <= 0 or args.timeout > 60:
        raise ValueError('Timeout must be positive and at most 60 seconds')
    if args.output.exists():
        raise ValueError('Refuse to overwrite a baseline')
    args.output.mkdir(parents=True)
    corpus = json.loads((args.workbook/'inventory.json').read_text(encoding='utf-8-sig'))
    metadata = dict(StartedAt=datetime.now(timezone.utc).isoformat(),
        AssemblySha256=hashlib.sha256(args.assembly.read_bytes()).hexdigest().upper(),
        ArchiveSha256=corpus['ArchiveSha256'],ImageCount=len(corpus['Images']),
        PerImageProcessTimeoutSeconds=args.timeout,
        Timing='Cold process end-to-end including host/model initialization; not warm pipeline latency',
        GroundTruthAvailable=False,Scope='Offline diagnostics only; no real connection; no cloud/network OCR')
    (args.output/'run-info.json').write_text(json.dumps(metadata,ensure_ascii=False,indent=2),encoding='utf-8')
    rows = []
    for item in corpus['Images']:
        image = args.workbook/item['RelativePath']
        if hashlib.sha256(image.read_bytes()).hexdigest().upper() != item['Sha256']:
            raise ValueError('Changed input: '+item['Id'])
        output = args.output/(item['Id']+'.json')
        error = args.output/(item['Id']+'.stderr.txt')
        row = dict(Id=item['Id'],Sha256=item['Sha256'],PreviouslySeen=bool(item['KnownPixelMatches']),
                   Width=item['Width'],Height=item['Height'])
        start = time.perf_counter()
        with output.open('wb') as stdout, error.open('wb') as stderr:
            process = subprocess.Popen([str(args.dotnet.resolve()),str(args.assembly.resolve()),'--selftest',str(image.resolve())],
                stdout=stdout,stderr=stderr,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            try:
                code = process.wait(timeout=args.timeout)
                row['Status'] = 'completed' if code == 0 else 'error'
                row['ExitCode'] = code
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
                row['Status'] = 'timeout'
        row['WallMs'] = round((time.perf_counter()-start)*1000,2)
        if row['Status'] == 'completed':
            try:
                r = json.loads(output.read_text(encoding='utf-8-sig'))
                attempts = r.get('Attempts',[])
                documents = [a['Document'] for a in attempts] or [r['Document']]
                code = r.get('SelectedCode') or {}
                row.update(App=r['AppName'],CodePresent=bool(r['DeviceCode']),PasswordPresent=bool(r['Password']),
                    Decision='Automatic' if r['Decision']==1 else 'Manual',AttemptCount=len(attempts),
                    AnyText=any(d['Blocks'] for d in documents),CodeConfidence=code.get('OcrConfidence'),
                    MinimumCodeConfidence=code.get('MinimumOcrConfidence'),ConvertedCode=code.get('HadCharacterConversion',False),
                    Issues=r.get('CrossCheckIssues',[]),Warnings=r['Warnings'],Reason=r['Reason'])
            except (KeyError,ValueError,TypeError) as ex:
                row.update(Status='invalid-output',Error=type(ex).__name__)
        rows.append(row)
        (args.output/'observations.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
        # Do not print device codes, passwords or image names to the conversation.
        print(json.dumps(dict(Id=row['Id'],Status=row['Status'],Ms=row['WallMs'])),flush=True)
    times = sorted(r['WallMs'] for r in rows)
    complete = [r for r in rows if r['Status']=='completed']
    summary = dict(Images=len(rows),Completed=len(complete),Timeouts=sum(r['Status']=='timeout' for r in rows),
        Errors=sum(r['Status'] not in {'completed','timeout'} for r in rows),
        Automatic=sum(r.get('Decision')=='Automatic' for r in rows),Manual=sum(r.get('Decision')=='Manual' for r in rows),
        NoCode=sum(not r['CodePresent'] for r in complete),UnknownSoftware=sum(r['App']=='请选择软件' for r in complete),
        NoText=sum(not r['AnyText'] for r in complete),TextButNoCode=sum(r['AnyText'] and not r['CodePresent'] for r in complete),
        CrossOcrConflicts=sum(any('不一致' in issue for issue in r['Issues']) for r in complete),
        KnownOverlap=sum(r['PreviouslySeen'] for r in rows),
        AverageColdMs=round(sum(times)/len(times),2),P95ColdMs=times[__import__('math').ceil(.95*len(times))-1],WorstColdMs=max(times),
        FieldAccuracy=None,WrongAutomatic=None,HighConfidenceButWrong=None,
        Explanation='Human labels pending; missing/unknown/conflict are observations, not proven errors. No correctness rate claimed.')
    (args.output/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(summary,ensure_ascii=True),flush=True)


if __name__ == '__main__':
    main()
