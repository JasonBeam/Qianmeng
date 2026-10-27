using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using DesktopBuddy.UI;
using DesktopBuddy;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private static async Task RunResourceDiagnosisAsync(Application app,string[] args)
    {
        MainWindow? pet=null;ControlCenterWindow? center=null;var rows=new List<object>();var success=false;
        Native.GetCursorPos(out var cursor);
        try
        {
            var mi=Array.IndexOf(args,"--model-file");var pi=Array.IndexOf(args,"--motion-profile-dir");
            if(mi<0||pi<0)throw new ArgumentException("Model and fresh profile required.");
            var profile=Path.GetFullPath(args[pi+1]);
            if(Directory.Exists(profile)||File.Exists(profile))throw new ArgumentException("Refuse existing profile.");
            pet=new MainWindow(new LocalStore(profile),_=>{},new ChatService(()=>null)){Topmost=false,Left=40,Top=80};
            pet.Settings.AutoHideFullscreen=false;app.MainWindow=pet;pet.Show();await Task.Delay(300);
            for(var i=0;i<150&&pet.IsModelLoading;i++)await Task.Delay(100);
            await pet.ImportPetModelAsync(Path.GetFullPath(args[mi+1]));
            await InputAsync(()=>Native.SetCursorPos(20,20));
            var player=GetField<RigScenePlayer>(pet,"_animationPlayer");
            async Task Sample(string phase)
            {
                await Task.Delay(700);using var process=Process.GetCurrentProcess();process.Refresh();
                var cpu=process.TotalProcessorTime;var clock=Stopwatch.StartNew();var start=player.FrameCount;var samples=new List<object>();
                var threadStarts=new Dictionary<int,double>();
                foreach(ProcessThread thread in process.Threads)try{threadStarts[thread.Id]=thread.TotalProcessorTime.TotalMilliseconds;}catch{}
                var sampleCount=args.Contains("--resource-stack-snapshot")?120:24;
                for(var i=0;i<sampleCount;i++)
                {
                    await Task.Delay(500);
                    samples.Add(new{seconds=clock.Elapsed.TotalSeconds,frames=player.FrameCount,lastFrameMilliseconds=player.LastFrameMilliseconds,
                        clip=player.Clips[player.ClipIndex].Name,action=player.CurrentCompanionAction.ToString(),fps=GetField<int>(player,"_fps"),
                        gestureRendering=pet.IsInteractionRendering,interactionState=pet.InteractionState,summary=pet.AnimationSummary});
                }
                process.Refresh();var threads=new List<object>();
                foreach(ProcessThread thread in process.Threads)try{threads.Add(new{id=thread.Id,cpuMilliseconds=thread.TotalProcessorTime.TotalMilliseconds-threadStarts.GetValueOrDefault(thread.Id)});}catch{}
                rows.Add(new{phase,elapsedSeconds=clock.Elapsed.TotalSeconds,renderedFrames=player.FrameCount-start,
                    oneCoreCpuPercent=(process.TotalProcessorTime-cpu).TotalSeconds/clock.Elapsed.TotalSeconds*100,
                    privateMiB=process.PrivateMemorySize64/1048576d,workingMiB=process.WorkingSet64/1048576d,
                    renderTier=System.Windows.Media.RenderCapability.Tier>>16,contextDetection=pet.Settings.ContextDetectionEnabled,threads,samples});
            }
            pet.OpenModelManager();center=app.Windows.OfType<ControlCenterWindow>().Single();await Sample("model-page-open");
            if(args.Contains("--resource-stack-snapshot")){success=true;return;}
            if(args.Contains("--resource-paused-focus-diagnosis"))
            {
                pet.PauseAnimation();center.Activate();await Sample("paused-model-page-foreground-A");
                var neutral=new Window{Title="Qianmeng paused focus fixture",Width=240,Height=90,Left=SystemParameters.WorkArea.Right-250,Top=20,Content=new TextBlock{Text="Bounded paused diagnostic",Margin=new Thickness(12)}};
                try
                {
                    neutral.Show();neutral.Activate();await Sample("paused-same-model-page-neutral-focused-B");
                    center.Activate();await Sample("paused-model-page-foreground-restored-A");
                }
                finally{neutral.Close();}
                success=true;return;
            }
            if(args.Contains("--resource-tree-diagnosis"))
            {
                pet.PauseAnimation();await Sample("paused-full-model-page-A");
                var page=GetField<StackPanel>(center,"_modelsPage");
                var controls=page.Children.OfType<StackPanel>().Single(p=>p.Children.OfType<TextBlock>().Any(t=>t.Text=="动作播放"));
                var index=page.Children.IndexOf(controls);
                var placeholder=new Border{Width=controls.ActualWidth,Height=controls.ActualHeight};
                try
                {
                    page.Children.Remove(controls);page.Children.Insert(index,placeholder);
                    await Sample("paused-same-layout-with-action-controls-detached-B");
                }
                finally{page.Children.Remove(placeholder);page.Children.Insert(index,controls);}
                await Sample("paused-full-model-page-restored-A");
                success=true;return;
            }
            if(args.Contains("--resource-focus-diagnosis"))
            {
                pet.PauseAnimation();await Sample("model-page-open-animation-paused");
                pet.PreviewAnimation("Human_Idle");
                var neutral=new Window{Title="Qianmeng resource focus fixture",Width=240,Height=90,Left=SystemParameters.WorkArea.Right-250,Top=20,Content=new TextBlock{Text="Bounded resource diagnostic",Margin=new Thickness(12)}};
                try{neutral.Show();neutral.Activate();await Sample("both-live-views-neutral-fixture-focused");}
                finally{neutral.Close();}
            }
            if(args.Contains("--resource-experiments"))
            {
                var desktopView=(PetModelView)pet.FindName("PetModel");var preview=GetField<PetModelView>(center,"_modelPreview");
                desktopView.Scene=null;await Sample("preview-only-with-live-sampler");desktopView.Scene=pet.ModelScene;
                foreach(var view in new[]{desktopView,preview}){view.ClipToBounds=false;((Viewport3D)view.Children[0]).ClipToBounds=false;}
                await Sample("both-views-without-duplicate-clipping");
                foreach(var view in new[]{desktopView,preview})RenderOptions.SetEdgeMode((Viewport3D)view.Children[0],EdgeMode.Aliased);
                await Sample("both-views-aliased-3d-edges");
                foreach(var view in new[]{desktopView,preview}){RenderOptions.SetEdgeMode((Viewport3D)view.Children[0],EdgeMode.Unspecified);view.ClipToBounds=true;((Viewport3D)view.Children[0]).ClipToBounds=true;}
            }
            center.Close();center=null;await Sample("pet-only-preview-closed");
            pet.PauseAnimation();await Sample("pet-only-animation-paused");success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            center?.Close();if(pet is not null)CloseModelPet(pet);Native.SetCursorPos(cursor.X,cursor.Y);
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="diagnostic-no-budget-waiver-no-capture-no-forced-GC",rows,checks=Results},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
}
