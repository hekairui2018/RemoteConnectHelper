using RemoteConnectHelper.V2.Ocr;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Application;
using SkiaSharp;

namespace RemoteConnectHelper.V2.Diagnostics;
public static class OrientationTests
{
    public static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);int passed=0,failed=0;
        async Task Check(string name,Func<Task<bool>> test){try{if(!await test())throw new Exception("unexpected result");passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        using var provider=new RapidOcrProvider();
        using var bitmap=new SKBitmap(900,430);using(var c=new SKCanvas(bitmap)){
            c.Clear(SKColors.White);using var font=new SKFont(SKTypeface.FromFamilyName("Arial"),34);
            using var paint=new SKPaint{Color=SKColors.Black,IsAntialias=true};
            c.DrawText("Remote desktop connection",40,70,font,paint);
            c.DrawText("Please check your device number",40,145,font,paint);
            c.DrawText("Temporary password and settings",40,220,font,paint);
            c.DrawText("Connection history and assistance",40,295,font,paint);
        }
        using var original=bitmap.Encode(SKEncodedImageFormat.Png,100);byte[] bytes=original.ToArray();
        foreach(int rotation in new[]{0,90,180,270}){
            using var rotated=ImagePreparation.Prepare(bytes,rotation,false);using var data=rotated.Encode(SKEncodedImageFormat.Png,100);var input=data.ToArray();
            File.WriteAllBytes(Path.Combine(output,$"input-{rotation}.png"),input);
            await Check($"direction only: {rotation}",async()=>{int found=await provider.DetectOrientationAsync(input,CancellationToken.None);Console.WriteLine($"rotation {rotation}: correction {found}");return found==(360-rotation)%360;});
        }
        await Check("blank image remains unchanged",async()=>{using var b=new SKBitmap(240,160);b.Erase(SKColors.White);using var d=b.Encode(SKEncodedImageFormat.Png,100);return await provider.DetectOrientationAsync(d.ToArray(),CancellationToken.None)==0;});
        await Check("ambiguous and insufficient direction votes stay unchanged",()=>Task.FromResult(
            PreviewOrientation.Choose([(90,.99),(270,.99)])==0&&PreviewOrientation.Choose([(90,.99)])==0&&PreviewOrientation.Choose([(90,.80),(90,.85)])==0));
        await Check("mark coordinates round trip at all quarter turns",()=>Task.FromResult(new[]{0,90,180,270}.All(r=>{
            var box=new Box(40,70,210,35);return ImagePreparation.ToOriginal(PreviewOrientation.ToDisplay(box,r,900,430),r,900,430)==box;
        })));
        await Check("preview orientation cannot fill fields, connect, or update a newer session",async()=>{
            int analyze=0,connect=0;await using var controller=new AssistantController((_,_,_)=>{analyze++;throw new Exception("field recognition called");},(_,_,_,_)=>{connect++;throw new Exception("connect called");});
            await controller.PreviewClipboardAsync(bytes);long revision=controller.Revision;
            controller.ApplyPreviewOrientation(bytes,90,revision);
            bool valid=controller.PreviewRotation==90&&controller.Result==null&&ReferenceEquals(controller.Image,bytes);
            await controller.NewManualAsync();controller.ApplyPreviewOrientation(bytes,180,revision);
            return valid&&controller.Result==null&&controller.Image==null&&analyze==0&&connect==0;
        });
        Console.WriteLine($"ORIENTATION RESULT {passed} passed, {failed} failed");return failed==0?0:1;
    }
}
