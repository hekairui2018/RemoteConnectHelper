"""Create an entirely local, prediction-free image annotation workbook.

No network, OCR, cloud APIs or image recognition. Images remain unchanged.
Answers are deliberately stored separately from algorithm outputs.
"""
import argparse
import hashlib
import json
import shutil
from collections import Counter
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def pixels(path):
    with Image.open(path) as image:
        image = ImageOps.exif_transpose(image).convert("RGB")
        return hashlib.sha256(str(image.size).encode() + image.tobytes()).hexdigest()


HTML = r'''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self' file: data:; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>远程截图 · 本机核对</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#f2f5fa;color:#24334b;font:15px 'Microsoft YaHei UI',sans-serif}
header{padding:18px 24px;background:#fff;display:flex;gap:16px;align-items:center;flex-wrap:wrap;border-bottom:1px solid #dce3ef}
h1{font-size:21px;margin:0}button,.file{background:#fff;border:1px solid #ccd6e7;border-radius:7px;padding:9px 13px;cursor:pointer;color:#24334b;font:inherit}
button.primary{background:#3565df;color:white;border-color:#3565df}button:disabled{opacity:.45;cursor:default}.muted{color:#718198;font-size:12px}
nav{display:flex;gap:6px;flex-wrap:wrap;padding:12px 24px}nav button{padding:6px 9px;font-size:12px}nav button.done{background:#e4f5eb;border-color:#8fc7a7}nav button.active{outline:2px solid #3565df}
main{display:grid;grid-template-columns:minmax(380px,1fr) 355px;gap:18px;padding:0 24px 24px}.card{background:#fff;border-radius:12px;padding:18px}
.toolbar{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-bottom:12px}.viewer{height:calc(100vh - 270px);min-height:380px;background:#e9eef6;overflow:auto;display:grid;place-items:center}
.viewer #stage{position:relative;flex-shrink:0}.viewer img{display:block;position:absolute;left:50%;top:50%;max-width:none;max-height:none;object-fit:contain}.viewer.zoom{display:block}
label{display:block;margin:13px 0 6px;font-size:13px}input,select,textarea{font:inherit;width:100%;border:1px solid #ccd6e7;background:#f8fafe;border-radius:6px;padding:9px;color:#1c2e4b}
input.code{font:22px Consolas,monospace}textarea{resize:vertical;min-height:58px}.pair{padding:0 0 15px;border-bottom:1px solid #e2e8f2}.status{margin:12px 0;line-height:1.7}.error{color:#b63b3b}
.footer{display:flex;gap:8px;position:sticky;bottom:0;background:white;padding:12px 0 0}.checkbox{display:flex;align-items:center;gap:9px}.checkbox input{width:auto}
a{color:#315fd1}#source{word-break:break-all}small{line-height:1.6;display:block}@media(max-width:950px){main{grid-template-columns:1fr}.viewer{height:500px}}
</style></head><body>
<header><div><h1>远程截图 · 本机核对</h1><div class="muted">只看原图填写；看不清就标记，不猜。页面不联网，不发起远程连接。</div></div><strong id="progress"></strong>
<button id="export">保存答案 / 进度</button><button id="import">读取已保存进度</button><input id="file" type="file" accept=".json" hidden></header>
<nav id="nav"></nav><main><section class="card"><div class="toolbar"><strong id="title"></strong><button id="zoom">原始大小</button><button id="left">向左转</button><button id="right">向右转</button><a id="open" target="_blank" rel="noopener">单独打开原图</a></div><div class="viewer" id="viewer"><div id="stage"><img id="image" alt="待核对的本机原图"></div></div><p class="muted" id="source"></p><small>这份页面没有加载任何 OCR 预测。绿色表示你已确认答案，不代表程序识别正确。核对时可按 Tab 切换输入框。</small></section>
<section class="card"><label>这张图的情况</label><select id="scene"><option value="">请选择</option><option value="remote">有远程信息，目标可确定</option><option value="multiple">有多个目标，无法确定要连哪一个</option><option value="unreadable">有远程信息，但看不清完整字段</option><option value="irrelevant">不含远程信息</option></select>
<div id="pairs"></div><button id="add">再记录一个目标</button>
<label>图片类型（按实际情况选择主要一类）</label><select id="category"><option value="">请选择</option><option>清晰截图</option><option>轻度模糊</option><option>中度模糊</option><option>强模糊</option><option>反光</option><option>手机拍屏</option><option>透视</option><option>强压缩</option><option>多数字干扰</option><option>多候选</option><option>截图嵌套</option><option>综合恶劣图片</option></select>
<label>备注（可不填）</label><textarea id="notes" placeholder="例如：密码有一位无法确定；上面是本机码，下面是历史输入"></textarea>
<div class="status"><label class="checkbox"><input id="confirmed" type="checkbox">我已对照原图确认，或明确标记看不清</label><div id="message" class="error"></div></div>
<div class="footer"><button id="prev">上一张</button><button class="primary" id="next">下一张</button></div>
</section></main>
<script>
const corpus=__CORPUS__, storageKey='remote-code-human-'+corpus.ArchiveSha256;
let at=0, rotation=0, zoom=false;
const emptyPair=()=>({App:'',Code:'',CodeStatus:'',Password:'',PasswordStatus:''});
let answers=corpus.Images.map(x=>({Id:x.Id,Sha256:x.Sha256,Scene:'',Category:'',Targets:[emptyPair()],Notes:'',Confirmed:false}));
const $=id=>document.getElementById(id);
function persist(){try{localStorage.setItem(storageKey,JSON.stringify(answers));}catch(e){$('message').textContent='浏览器未保存自动进度，请点击“保存答案 / 进度”。';}}
function merge(saved){if(!Array.isArray(saved)||saved.length>answers.length)throw Error('答案格式不正确');const pending=[],ids=new Set();for(const x of saved){const idx=corpus.Images.findIndex(i=>i.Id===x.Id&&i.Sha256===x.Sha256);if(idx<0||ids.has(x.Id))throw Error('图片清单不同或有重复，请勿混用另一批答案');if(!Array.isArray(x.Targets)||x.Targets.length>20)throw Error('目标格式不正确');ids.add(x.Id);pending.push([idx,{...answers[idx],...x}]);}for(const [idx,value] of pending)answers[idx]=value;}
try{const raw=localStorage.getItem(storageKey);if(raw)merge(JSON.parse(raw));}catch(e){}
function setField(object,key,control){control.value=object[key]||'';control.addEventListener('input',()=>{object[key]=control.value;answers[at].Confirmed=false;$('confirmed').checked=false;persist();drawNav();});}
function optionSelect(values){const s=document.createElement('select');for(const [value,label] of values){const o=document.createElement('option');o.value=value;o.textContent=label;s.append(o);}return s;}
function field(parent,title,control){const l=document.createElement('label');l.textContent=title;parent.append(l,control);}
function pairUi(pair,index){const box=document.createElement('div');box.className='pair';const h=document.createElement('h3');h.textContent='目标 '+(index+1);box.append(h);
const app=optionSelect([['','请选择软件'],['Sunlogin','向日葵'],['ToDesk','ToDesk'],['Unknown','无法确定软件']]);setField(pair,'App',app);field(box,'软件',app);
const cs=optionSelect([['','请选择设备码状态'],['visible','完整可读'],['partial','只看清一部分'],['unreadable','看不清'],['absent','图中没有设备码']]);setField(pair,'CodeStatus',cs);field(box,'设备码状态',cs);
const c=document.createElement('input');c.className='code';c.autocomplete='off';c.spellcheck=false;setField(pair,'Code',c);field(box,'设备码（仅填写看清的原文）',c);
const ps=optionSelect([['','请选择密码状态'],['visible','完整可读'],['hidden','被圆点隐藏'],['absent','图中没有密码'],['partial','只看清一部分'],['unreadable','看不清']]);setField(pair,'PasswordStatus',ps);field(box,'密码状态',ps);
const p=document.createElement('input');p.autocomplete='off';p.spellcheck=false;setField(pair,'Password',p);field(box,'密码（保留大小写，0/O 按原图核对）',p);
if(answers[at].Targets.length>1){const remove=document.createElement('button');remove.textContent='移除此目标';remove.onclick=()=>{answers[at].Targets.splice(index,1);answers[at].Confirmed=false;persist();show();};box.append(remove);}$('pairs').append(box);}
function drawNav(){$('nav').replaceChildren();answers.forEach((x,i)=>{const b=document.createElement('button');b.textContent=corpus.Images[i].Id;b.className=(x.Confirmed?'done ':'')+(i===at?'active':'');b.onclick=()=>{at=i;show();};$('nav').append(b);});$('progress').textContent='已确认 '+answers.filter(x=>x.Confirmed).length+' / '+answers.length;}
function show(){rotation=0;zoom=false;const image=corpus.Images[at],a=answers[at];$('title').textContent=image.Id+' · '+(at+1)+' / '+answers.length;$('image').src=image.RelativePath;$('viewer').className='viewer';$('zoom').textContent='原始大小';$('open').href=image.RelativePath;$('source').textContent=image.OriginalName+' · '+image.Width+' × '+image.Height;
$('scene').value=a.Scene;$('category').value=a.Category;$('notes').value=a.Notes;$('confirmed').checked=a.Confirmed;$('pairs').replaceChildren();a.Targets.forEach(pairUi);$('prev').disabled=at===0;$('next').disabled=at===answers.length-1;$('message').textContent='';drawNav();}
function validate(a){if(!a.Scene)return'请选择图片情况';if(!a.Category)return'请选择主要图片类型';if(a.Scene==='irrelevant')return'';for(const p of a.Targets){if(!p.App||!p.CodeStatus||!p.PasswordStatus)return'请补齐软件、设备码状态和密码状态';if(p.CodeStatus==='visible'&&!/^[0-9]{9,10}$/.test(p.Code.replace(/\s/g,'')))return'完整设备码应为9或10位数字；看不清请改选状态，不要猜';if(p.App==='ToDesk'&&p.CodeStatus==='visible'&&p.Code.replace(/\s/g,'').length!==9)return'ToDesk 完整设备码应为9位';if(p.PasswordStatus==='visible'&&!p.Password)return'完整密码不能为空；没有或隐藏请选择对应状态';if(['hidden','absent'].includes(p.PasswordStatus)&&p.Password)return'密码状态为隐藏/没有时，请清空密码内容';}return'';}
for(const [id,key] of [['scene','Scene'],['category','Category'],['notes','Notes']])$(id).oninput=()=>{answers[at][key]=$(id).value;answers[at].Confirmed=false;$('confirmed').checked=false;persist();drawNav();};
$('confirmed').onchange=()=>{const error=$('confirmed').checked?validate(answers[at]):'';$('message').textContent=error;if(error)$('confirmed').checked=false;answers[at].Confirmed=$('confirmed').checked;answers[at].VerifiedAt=answers[at].Confirmed?new Date().toISOString():null;persist();drawNav();};
$('prev').onclick=()=>{if(at>0){at--;show();}};$('next').onclick=()=>{if(at<answers.length-1){at++;show();}};
$('add').onclick=()=>{answers[at].Targets.push(emptyPair());answers[at].Confirmed=false;persist();show();};
$('zoom').onclick=()=>{zoom=!zoom;$('viewer').className=zoom?'viewer zoom':'viewer';$('zoom').textContent=zoom?'适应窗口':'原始大小';renderImage();};
function renderImage(){const image=$('image'),w=image.naturalWidth||corpus.Images[at].Width,h=image.naturalHeight||corpus.Images[at].Height,swapped=Math.abs(rotation)%180===90;const rw=swapped?h:w,rh=swapped?w:h;const scale=zoom?1:Math.min(($('viewer').clientWidth||800)/rw,($('viewer').clientHeight||500)/rh,1);$('stage').style.width=rw*scale+'px';$('stage').style.height=rh*scale+'px';image.style.width=w*scale+'px';image.style.height=h*scale+'px';image.style.transform='translate(-50%,-50%) rotate('+rotation+'deg)';}
function rotate(delta){rotation=(rotation+delta)%360;renderImage();}$('left').onclick=()=>rotate(-90);$('right').onclick=()=>rotate(90);$('image').onload=renderImage;window.addEventListener('resize',renderImage);
$('export').onclick=()=>{const confirmed=answers.filter(x=>x.Confirmed);const invalid=confirmed.some(x=>validate(x));if(invalid){$('message').textContent='部分确认项不完整，请重新检查';return;}
const payload={SchemaVersion:1,ArchiveSha256:corpus.ArchiveSha256,ExportedAt:new Date().toISOString(),Frozen:confirmed.length===answers.length,ConfirmedCount:confirmed.length,Total:answers.length,HumanAnswers:answers};
const url=URL.createObjectURL(new Blob([JSON.stringify(payload,null,2)],{type:'application/json;charset=utf-8'}));const a=document.createElement('a');a.href=url;a.download='远程码_人工答案_20261016.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),10000);};
$('import').onclick=()=>$('file').click();$('file').onchange=async e=>{try{const text=await e.target.files[0].text(),data=JSON.parse(text);if(data.ArchiveSha256!==corpus.ArchiveSha256||data.Total!==answers.length)throw Error('这不是同一压缩包的答案');merge(data.HumanAnswers);persist();show();}catch(error){$('message').textContent='读取失败：'+error.message;}};
show();
</script></body></html>'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--images', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--known', type=Path)
    parser.add_argument('--archive-sha', required=True)
    args = parser.parse_args()
    files = sorted((p for p in args.images.iterdir() if p.suffix.lower() in {'.jpg', '.jpeg', '.png', '.bmp'}), key=lambda p:p.name.casefold())
    if not files:
        raise ValueError('No images')
    if args.output.exists():
        raise ValueError('Output exists; refuse to overwrite human work')
    assets = args.output / 'images'
    assets.mkdir(parents=True)
    known_bytes, known_pixels = {}, {}
    if args.known:
        for path in args.known.iterdir():
            if path.suffix.lower() in {'.png','.jpg','.jpeg'}:
                known_bytes.setdefault(sha(path), []).append(path.name)
                known_pixels.setdefault(pixels(path), []).append(path.name)
    entries = []
    for number, path in enumerate(files, 1):
        identity = f'R{number:03d}'
        destination = assets / (identity + path.suffix.lower())
        shutil.copy2(path, destination)
        with Image.open(path) as image:
            oriented = ImageOps.exif_transpose(image).convert('L')
            small = np.asarray(oriented.resize((min(512,oriented.width), min(512,oriented.height))), dtype=np.float32)
            edge = np.diff(small,axis=0).var() + np.diff(small,axis=1).var()
            digest, pixel_digest = sha(path), pixels(path)
            entries.append(dict(Id=identity,OriginalName=path.name,RelativePath='images/'+destination.name,
                Sha256=digest,PixelSha256=pixel_digest,Width=image.width,Height=image.height,
                Orientation=image.getexif().get(274),Bytes=path.stat().st_size,
                Brightness=round(float(small.mean()),2),ContrastStd=round(float(small.std()),2),
                EdgeVarianceProxy=round(float(edge),2),
                KnownExactMatches=known_bytes.get(digest,[]),KnownPixelMatches=known_pixels.get(pixel_digest,[])))
    corpus = dict(SchemaVersion=1,ArchiveSha256=args.archive_sha.upper(),Images=entries,
                  AnswersAvailable=False,Source='User archive; not synthetic',
                  Notice='Image quality proxies are not correctness probabilities. Human answers must be independently verified.')
    (args.output/'inventory.json').write_text(json.dumps(corpus,ensure_ascii=False,indent=2),encoding='utf-8')
    data = json.dumps(corpus,ensure_ascii=True).replace('<','\\u003c')
    (args.output/'开始核对.html').write_text(HTML.replace('__CORPUS__',data),encoding='utf-8')
    counts = Counter(e['PixelSha256'] for e in entries)
    print(json.dumps(dict(Images=len(entries),UniqueDecodedImages=len(counts),
        PreviouslySeen=sum(bool(e['KnownPixelMatches']) for e in entries),
        MaxPixels=max(e['Width']*e['Height'] for e in entries),
        Workbook=str(args.output/'开始核对.html')),ensure_ascii=True))


if __name__ == '__main__':
    main()
