using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DesktopBuddy;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static async Task RunLiveActionReviewAsync(Application app,string[] args)
    {
        Native.GetCursorPos(out var cursor);MainWindow? pet=null;var success=false;
        EventHandler? renderObserver=null;
        var clips=new List<object>();
        try
        {
            var modelAt=Array.IndexOf(args,"--model-file");var profileAt=Array.IndexOf(args,"--motion-profile-dir");
            if(modelAt<0||profileAt<0)throw new ArgumentException("Model and new isolated profile required.");
            var profile=Path.GetFullPath(args[profileAt+1]);
            if(Directory.Exists(profile)||File.Exists(profile))throw new ArgumentException("Refusing to reuse profile.");
            pet=new MainWindow(new LocalStore(profile),_=>{},new ChatService(()=>null)){Topmost=false,Left=40,Top=40};
            pet.Settings.AutoHideFullscreen=false;pet.Settings.ContextDetectionEnabled=false;pet.Settings.AutomaticActivityAnimation=false;
            app.MainWindow=pet;pet.Show();await Task.Delay(250);
            for(var i=0;i<150&&pet.IsModelLoading;i++)await Task.Delay(100);
            await pet.ImportPetModelAsync(Path.GetFullPath(args[modelAt+1]));
            await InputAsync(()=>Native.SetCursorPos((int)SystemParameters.WorkArea.Right-10,(int)SystemParameters.WorkArea.Top+10));
            var player=GetField<RigScenePlayer>(pet,"_animationPlayer");
            var idleClip=RigClipResolver.FindIdle(player.Clips);
            if(idleClip<0)throw new InvalidDataException("The imported model has no resolvable idle clip.");
            pet.PreviewAnimation(player.Clips[idleClip].Name);await Task.Delay(300);
            var sampler=GetField<RigSampler>(player,"_sampler");
            var rig=GetField<RigAnimation>(sampler,"_rig");var hips=Array.FindIndex(rig.Nodes,n=>n.Name=="mixamorig:Hips");
            List<object>? activeRenderSamples=null;Stopwatch? activeClock=null;var lastRendered=pet.AnimationFrameCount;
            renderObserver=(_,_)=>
            {
                if(activeRenderSamples is null||activeClock is null||pet.AnimationFrameCount==lastRendered)return;
                lastRendered=pet.AnimationFrameCount;
                activeRenderSamples.Add(new{seconds=activeClock.Elapsed.TotalSeconds,hipY=GetField<Matrix4x4[]>(sampler,"_world")[hips].M42,
                    correctionMeters=sampler.SoleSupportCorrectionMeters,renderedFrame=lastRendered,actionSeconds=GetField<double>(player,"_companionActionElapsed")});
            };
            CompositionTarget.Rendering+=renderObserver;
            var at=Array.IndexOf(args,"--live-actions");
            var definitions=at>=0?args[at+1].Split(',').Select(id=>CompanionActionMotion.Library.First(d=>d.Id==id)).ToArray():
                CompanionActionMotion.Library.Where(d=>d.Kind!=CompanionActionKind.Idle).ToArray();
            foreach(var definition in definitions)
            {
                pet.PlayCompanionAction(CompanionActionKind.Idle);await Task.Delay(280);
                var native=RigClipResolver.FindAction(player.Clips,definition.Kind);
                var duration=native>=0?player.Clips[native].Duration/RigClipResolver.PlaybackRate(player.Clips[native]):definition.DurationSeconds;
                pet.PlayCompanionAction(definition.Kind);var clock=Stopwatch.StartNew();
                var renderSamples=new List<object>();activeRenderSamples=renderSamples;activeClock=clock;
                var firstFrame=pet.AnimationFrameCount;var frames=new List<object>();var finite=true;var sample=0;var nextCaptureSeconds=0d;
                while(clock.Elapsed.TotalSeconds<duration+.6)
                {
                    finite&=sampler.Positions.All(mesh=>mesh.All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
                    var seconds=clock.Elapsed.TotalSeconds;
                    if(seconds>=nextCaptureSeconds)
                    {
                        var image=Path.Combine(Path.GetDirectoryName(_report)!,Path.GetFileNameWithoutExtension(_report)+$".{definition.Id}.{sample++:D3}.png");
                        SaveWindowImage(pet,image);
                        frames.Add(new{seconds,image=Path.GetFileName(image),
                            action=player.CurrentCompanionAction.ToString(),clip=player.Clips[player.ClipIndex].Name,
                            actionSeconds=GetField<double>(player,"_companionActionElapsed"),
                            correctionMeters=sampler.SoleSupportCorrectionMeters,renderedFrames=pet.AnimationFrameCount});
                        nextCaptureSeconds=seconds+.75;
                    }
                    await Task.Delay(16);
                }
                var returned=player.CurrentCompanionAction==CompanionActionKind.None&&player.Clips[player.ClipIndex].Name==player.Clips[idleClip].Name;
                clips.Add(new{name=definition.Id,duration,source=native>=0?player.Clips[native].Name:"procedural-on-"+player.Clips[idleClip].Name,
                    elapsed=clock.Elapsed.TotalSeconds,renderedFrames=pet.AnimationFrameCount-firstFrame,returned,
                    finalAction=player.CurrentCompanionAction.ToString(),finalClip=player.Clips[player.ClipIndex].Name,
                    finalActionSeconds=GetField<double>(player,"_companionActionElapsed"),frames,renderSamples});
                activeRenderSamples=null;activeClock=null;
                File.WriteAllText(Path.ChangeExtension(_report,".progress.json"),JsonSerializer.Serialize(new{complete=false,clips},new JsonSerializerOptions{WriteIndented=true}));
                Check("actual-action-advances-"+definition.Id,pet.AnimationFrameCount-firstFrame>=3,"No real player cadence.");
                Check("actual-action-finite-"+definition.Id,finite,"Invalid pose during normal playback.");
                Check("actual-action-returns-"+definition.Id,returned,"Action did not return to its resolved idle base.");
            }
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            if(renderObserver is not null)CompositionTarget.Rendering-=renderObserver;
            try{if(pet is not null)CloseModelPet(pet);Native.SetCursorPos(cursor.X,cursor.Y);}catch{}
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="real-MainWindow-public-action-path-normal-speed-with-capture-overhead-not-installed-EXE",checks=Results,clips},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
}
