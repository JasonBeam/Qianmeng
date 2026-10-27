using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using DesktopBuddy;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private static async Task RunLiveMotionChecksAsync(Application app,string[] args)
    {
        var at=Array.IndexOf(args,"--model-file");
        var profileAt=Array.IndexOf(args,"--motion-profile-dir");
        var preserveProfile=profileAt>=0;
        var temp=preserveProfile&&profileAt+1<args.Length?Path.GetFullPath(args[profileAt+1]):Path.Combine(Path.GetTempPath(),"Qianmeng-LiveMotion-"+Guid.NewGuid().ToString("N"));
        var namesAt=Array.IndexOf(args,"--live-motion-clips");
        var cyclesAt=Array.IndexOf(args,"--live-motion-cycles");
        var cycles=cyclesAt>=0&&cyclesAt+1<args.Length?Math.Clamp(int.Parse(args[cyclesAt+1]),1,30):1;
        var captureWindows=!args.Contains("--live-motion-no-capture");
        MainWindow? pet=null;var success=false;var clips=new List<object>();
        try
        {
            if(at<0)throw new ArgumentException("--model-file is required.");
            if(preserveProfile&&(profileAt+1>=args.Length||Directory.Exists(temp)||File.Exists(temp)))throw new ArgumentException("--motion-profile-dir requires a new isolated directory.");
            pet=new MainWindow(new LocalStore(temp),_=>{},new ChatService(()=>null)){Topmost=false,ShowActivated=false,Left=40,Top=40};
            app.MainWindow=pet;pet.Show();await Task.Delay(300);
            for(var i=0;i<150&&pet.IsModelLoading;i++)await Task.Delay(100);
            await pet.ImportPetModelAsync(Path.GetFullPath(args[at+1]));
            pet.Settings.AutoHideFullscreen=false;pet.Settings.AutomaticActivityAnimation=false;
            var player=GetField<RigScenePlayer>(pet,"_animationPlayer");var sampler=GetField<RigSampler>(player,"_sampler");
            var native=player.Clips;
            var selectedNames=namesAt>=0&&namesAt+1<args.Length?args[namesAt+1].Split(','):new[]{"Human_Idle","Human_Greeting","Human_Nod","Human_Listening","Human_Bow","Human_Walk"};
            foreach(var name in selectedNames)
            {
                var clip=native.First(c=>c.Name==name);
                // Capture each authored clip from its own first frame. Switching
                // while playing blends 240 ms from the previous clip and can make
                // a nod appear to wave or an idle appear to run in the contact sheet.
                pet.PauseAnimation();
                pet.PlayCompanionAction(CompanionActionKind.Idle);
                player.SetCompanionGesture(Vector3.Zero,0,Vector3.Zero,default,false);
                player.Select(Array.FindIndex(native,c=>c.Name==name));
                pet.PreviewAnimation(name);
                var start=pet.AnimationFrameCount;var clock=Stopwatch.StartNew();var frames=new List<object>();var finite=true;var sample=0;
                using var process=Process.GetCurrentProcess();process.Refresh();
                var cpuStart=process.TotalProcessorTime;var privateStart=process.PrivateMemorySize64;var workingStart=process.WorkingSet64;
                var privatePeak=privateStart;var workingPeak=workingStart;
                while(clock.Elapsed.TotalSeconds<clip.Duration*cycles)
                {
                    var path=Path.Combine(Path.GetDirectoryName(_report)!,Path.GetFileNameWithoutExtension(_report)+$".{name}.{sample++:D3}.png");
                    if(captureWindows)SaveWindowImage(pet,path);
                    finite&=sampler.Positions.All(m=>m.All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
                    process.Refresh();privatePeak=Math.Max(privatePeak,process.PrivateMemorySize64);workingPeak=Math.Max(workingPeak,process.WorkingSet64);
                    var animationSeconds=GetField<double>(player,"_seconds");
                    frames.Add(new{seconds=clock.Elapsed.TotalSeconds,image=captureWindows?Path.GetFileName(path):null,renderedFrames=pet.AnimationFrameCount,animationSeconds,phaseSeconds=animationSeconds%clip.Duration});
                    await Task.Delay(75);
                }
                Check("live-"+name+"-advances",pet.AnimationFrameCount-start>=3,"The live WPF timer failed to advance "+name);
                Check("live-"+name+"-finite",finite,"The live pose became invalid in "+name);
                process.Refresh();
                clips.Add(new{name,clip.Duration,cycles,firstKeySeconds=clip.Tracks.Min(t=>t.Times[0]),elapsed=clock.Elapsed.TotalSeconds,renderedFrames=pet.AnimationFrameCount-start,
                    resources=new{includesWindowCaptureOverhead=captureWindows,privateStart,privatePeak,privateEnd=process.PrivateMemorySize64,workingStart,workingPeak,workingEnd=process.WorkingSet64,
                        singleCoreCpuPercent=(process.TotalProcessorTime-cpuStart).TotalSeconds/clock.Elapsed.TotalSeconds*100},frames});
            }
            pet.PreviewAnimation("Human_Greeting");await Task.Delay(1300);
            var visible=sampler.Positions.Select(m=>(Vector3[])m.Clone()).ToArray();
            pet.PreviewAnimation("Human_Nod");await Task.Delay(35);
            var displacement=sampler.Positions.SelectMany((mesh,m)=>mesh.Select((p,v)=>Vector3.Distance(p,visible[m][v]))).Max();
            Check("live-interruption-has-no-first-frame-snap",displacement<.08,"The first live transition frame jumped more than 8cm: "+displacement);
            await Task.Delay(70);pet.PreviewAnimation("Human_Listening");await Task.Delay(70);pet.PreviewAnimation("Human_Idle");
            await Task.Delay(400);
            Check("live-repeated-interruption-finite",sampler.Positions.All(m=>m.All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z))),"Repeated live interruptions invalidated the skeleton.");
            pet.PauseAnimation();var paused=pet.AnimationFrameCount;await Task.Delay(400);
            Check("live-pause-stops-player",pet.AnimationFrameCount==paused,"Paused playback kept rendering.");
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            if(pet is not null)CloseModelPet(pet);
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="live-WPF-timer-normal-speed-isolated-clips-not-installed-EXE",checks=Results,clips},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
}
