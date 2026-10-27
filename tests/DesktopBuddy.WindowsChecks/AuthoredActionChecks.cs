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
    private static async Task RunAuthoredActionChecksAsync(Application app,string[] args)
    {
        Native.GetCursorPos(out var originalCursor);
        MainWindow? pet=null;var success=false;var metrics=new List<object>();
        try
        {
            var modelAt=Array.IndexOf(args,"--model-file");var profileAt=Array.IndexOf(args,"--motion-profile-dir");
            if(modelAt<0||profileAt<0||modelAt+1>=args.Length||profileAt+1>=args.Length)throw new ArgumentException("Model and new isolated profile are required.");
            var profile=Path.GetFullPath(args[profileAt+1]);
            if(Directory.Exists(profile)||File.Exists(profile))throw new ArgumentException("Refusing to reuse profile.");
            var source=Path.GetFullPath(args[modelAt+1]);
            pet=new MainWindow(new LocalStore(profile),_=>{},new ChatService(()=>null))
            {Topmost=false,Left=SystemParameters.WorkArea.Right-360,Top=SystemParameters.WorkArea.Bottom-500};
            pet.Settings.AutoHideFullscreen=false;pet.Settings.AutomaticActivityAnimation=false;pet.Settings.ContextDetectionEnabled=false;
            app.MainWindow=pet;pet.Show();
            await Task.Delay(250);
            for(var i=0;i<150&&pet.IsModelLoading;i++)await Task.Delay(100);
            await pet.ImportPetModelAsync(source);
            await InputAsync(()=>Native.SetCursorPos(20,20));
            pet.PreviewAnimation("Human_Idle");await Task.Delay(350);
            var player=GetField<RigScenePlayer>(pet,"_animationPlayer");
            var sampler=GetField<RigSampler>(player,"_sampler");
            var asset=GlbReader.Load(source);
            Check("authored-default-idle-preferred",player.Clips[player.ClipIndex].Name=="Human_Idle","Legacy Idle was selected ahead of the authored idle.");
            Check("authored-drag-running-preferred",player.Clips[GetField<int>(sampler,"_nativeRunningClip")].Name=="Human_Run","Drag sampling still selects legacy Running.");
            Check("authored-resolver-retains-generic-run-fallback",RigClipResolver.FindRunning([new RigClip("RunCycle",1,[])])==0&&RigClipResolver.FindRunning([new RigClip("RunForward",1,[])])==0,"Legacy activity run substrings no longer resolve.");
            Check("authored-resolver-rejects-unplayable",RigClipResolver.FindRunning([new RigClip("Human_Run",0,[]),new RigClip("RunCycle",1,[])])==1,"An empty authored clip blocked a playable fallback.");
            var authoredActions=new List<(CompanionActionKind Kind,string Name)>{(CompanionActionKind.Waving,"Human_Greeting"),(CompanionActionKind.Nodding,"Human_Nod"),(CompanionActionKind.Listening,"Human_Listening"),(CompanionActionKind.Running,"Human_Run")};
            foreach(var extra in new[]{(CompanionActionKind.HeadScratching,"Human_HeadScratch"),(CompanionActionKind.DoubleWaving,"Human_DoubleWave")})
                if(asset.Rig!.Clips.Any(c=>c.Name==extra.Item2))authoredActions.Add(extra);
            foreach(var (kind,name) in authoredActions)
            {
                pet.PlayCompanionAction(kind);await Task.Delay(340);
                var index=player.ClipIndex;
                Check("authored-button-"+kind,player.Clips[index].Name==name&&GetField<int>(player,"_nativeActionClip")==index,"Action button did not select "+name);
                var seconds=GetField<double>(player,"_seconds");
                var reference=new RigSampler(asset);reference.Sample(index,seconds);
                var error=sampler.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,reference.Positions[i][j]))).Max();
                Check("authored-button-no-procedural-double-"+kind,error<.00001,"Actual action differs from authored sampler by "+error);
                metrics.Add(new{phase="action-button",kind=kind.ToString(),clip=player.Clips[index].Name,seconds,maxVertexError=error});
                pet.PlayCompanionAction(CompanionActionKind.Idle);await Task.Delay(300);
            }
            var model=(FrameworkElement)pet.FindName("PetModel");var point=ElementPoint(model);
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Thread.Sleep(50);Native.LeftDown();Thread.Sleep(70);Native.LeftUp();});
            await Task.Delay(330);
            Check("authored-real-click-selects-wave",pet.InteractionActionKind==CompanionActionKind.Waving&&GetField<int>(player,"_interactionClip") is var clickClip&&clickClip>=0&&player.Clips[clickClip].Name=="Human_Greeting","Actual click did not use the authored greeting.");
            Check("authored-real-click-no-procedural-overlay",sampler.CompanionInteractionAction==default,"Authored click also applies the old procedural wave.");
            SaveWindowImage(pet,Path.ChangeExtension(_report,".click-wave.png"));
            await InputAsync(()=>Native.SetCursorPos(20,20));var beforeFrames=pet.AnimationFrameCount;await Task.Delay(180);
            Check("authored-pointer-leave-keeps-native-wave",GetField<int>(player,"_interactionClip")>=0&&pet.AnimationFrameCount-beforeFrames>=3,"Leaving the pet lost the pointer-owned native action or its cadence.");
            var playerClock=GetField<Stopwatch>(player,"_clock");
            SetField(player,"_nextAmbientAction",playerClock.Elapsed.TotalSeconds-1);
            var initialSeconds=GetField<double>(player,"_interactionClipSeconds");
            pet.SaveAnimationOptions(true,1.5,30,"Human_Idle");
            Check("authored-pointer-speed-change-preserves-phase",Math.Abs(GetField<double>(player,"_interactionClipSeconds")-initialSeconds)<.08,"Changing speed rescaled elapsed history.");
            await Task.Delay(250);
            var fastSeconds=GetField<double>(player,"_interactionClipSeconds");
            Check("authored-pointer-fast-speed-advances-once",fastSeconds-initialSeconds is >.26 and <.50,"Fast pointer playback did not accumulate delta at 1.5x: "+(fastSeconds-initialSeconds));
            Check("authored-pointer-blocks-expired-ambient-deadline",GetField<int>(player,"_nativeActionClip")==-1&&!GetField<bool>(player,"_ambientAction")&&GetField<int>(player,"_interactionClip")>=0,"Ambient scheduling stole the pointer action.");
            pet.SaveAnimationOptions(true,.5,30,"Human_Idle");await Task.Delay(250);
            var slowSeconds=GetField<double>(player,"_interactionClipSeconds");
            Check("authored-pointer-slow-speed-preserves-history",slowSeconds-fastSeconds is >.06 and <.23,"Slowing playback jumped backward or truncated it: "+(slowSeconds-fastSeconds));
            metrics.Add(new{phase="pointer-mid-action-speed",initialSeconds,fastSeconds,slowSeconds});
            pet.SaveAnimationOptions(true,1,30,"Human_Idle");
            var visible=sampler.Positions.Select(m=>(Vector3[])m.Clone()).ToArray();
            pet.PlayCompanionAction(CompanionActionKind.Nodding);await Task.Delay(35);
            var jump=sampler.Positions.SelectMany((m,i)=>m.Select((p,j)=>Vector3.Distance(p,visible[i][j]))).Max();
            Check("authored-pointer-to-manual-no-snap",jump<.08&&GetField<int>(player,"_interactionClip")==-1,"Pointer to manual action snapped or retained the old owner: "+jump);
            Check("authored-pointer-release-defers-ambient",GetField<double>(player,"_nextAmbientAction")>playerClock.Elapsed.TotalSeconds+10,"Pointer release immediately resurrected an expired ambient deadline.");
            await Task.Delay(1100);
            Check("authored-one-shot-returns-idle",player.CurrentCompanionAction==CompanionActionKind.None&&player.Clips[player.ClipIndex].Name=="Human_Idle","One-shot did not return to its base exactly once.");
            Check("authored-one-shot-return-does-not-loop-ended-clip",GetField<int>(player,"_fromClip")==-1,"Returning from a one-shot blended its wrapped beginning into the exit.");
            pet.PlayCompanionAction(CompanionActionKind.Listening);
            SetField(player,"_ambientAction",true);await Task.Delay(180);
            Check("authored-ambient-not-self-cancelled",GetField<int>(player,"_nativeActionClip")>=0&&player.CurrentCompanionAction==CompanionActionKind.Listening,"A native ambient action cancelled itself because its clip is not idle.");
            point=ElementPoint(model);
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Native.LeftDown();});await Task.Delay(100);
            for(var step=1;step<=8;step++)
            {var dx=step*14;await InputAsync(()=>Native.SetCursorPos(point.X-dx,point.Y));await Task.Delay(35);}
            await Task.Delay(60);
            Check("authored-real-drag-cancels-old-native-owner",pet.InteractionState=="dragging"&&GetField<int>(player,"_nativeActionClip")==-1&&GetField<CompanionActionKind>(player,"_companionAction")==CompanionActionKind.None,"Drag retained an ambient/manual action owner.");
            Check("authored-real-drag-uses-new-run",player.Clips[GetField<int>(sampler,"_nativeRunningClip")].Name=="Human_Run"&&sampler.CompanionInteractionAction.NativeRunningWeight>.01,"Real drag did not reach the authored gait sampler.");
            SaveWindowImage(pet,Path.ChangeExtension(_report,".drag.png"));
            var leftRun=pet.InteractionActionKind;
            for(var step=1;step<=8;step++)
            {var dx=-112+step*16;await InputAsync(()=>Native.SetCursorPos(point.X+dx,point.Y));await Task.Delay(35);}
            await Task.Delay(40);
            Check("authored-real-drag-reverses-gait",leftRun==CompanionActionKind.RunningLeft&&pet.InteractionActionKind==CompanionActionKind.RunningRight&&sampler.CompanionInteractionAction.NativeRunningDirection>0,"Real pointer reversal did not switch the native gait direction.");
            SaveWindowImage(pet,Path.ChangeExtension(_report,".drag-right.png"));
            await InputAsync(Native.LeftUp);await Task.Delay(650);
            Check("authored-drag-exit-clears-override",!sampler.CompanionActionOverride&&GetField<int>(player,"_interactionClip")==-1,"Drag exit left an override active.");
            await InputAsync(()=>Native.SetCursorPos(20,20));await Task.Delay(100);
            pet.PlayCompanionAction(CompanionActionKind.Waving);await Task.Delay(150);
            var manualSeconds=GetField<double>(player,"_seconds");
            pet.SaveAnimationOptions(true,.5,15,"Human_Idle");
            Check("authored-manual-save-speed-fps-keeps-owner",player.CurrentCompanionAction==CompanionActionKind.Waving&&player.Clips[player.ClipIndex].Name=="Human_Greeting"&&GetField<double>(player,"_seconds")==manualSeconds,"Saving playback options cancelled or restarted a manual native action.");
            await Task.Delay(280);
            var manualSlowSeconds=GetField<double>(player,"_seconds");
            Check("authored-manual-speed-applied-once",manualSlowSeconds-manualSeconds is >.07 and <.22,"Manual native speed did not advance continuously at .5x: "+(manualSlowSeconds-manualSeconds));
            pet.PauseAnimation();
            var pausedFrames=pet.AnimationFrameCount;var pausedSeconds=GetField<double>(player,"_seconds");await Task.Delay(350);
            Check("authored-pause-freezes-time-and-render",pet.AnimationFrameCount==pausedFrames&&GetField<double>(player,"_seconds")==pausedSeconds&&!pet.IsInteractionRendering,"Pause leaked action rendering/time.");
            Check("authored-pause-retains-manual-owner",player.CurrentCompanionAction==CompanionActionKind.Waving&&GetField<int>(player,"_nativeActionClip")>=0,"Pause discarded the manual native action.");
            pet.SaveAnimationOptions(true,1,30,"Human_Idle");await Task.Delay(180);
            Check("authored-resume-retains-manual-phase",player.CurrentCompanionAction==CompanionActionKind.Waving&&GetField<double>(player,"_seconds")>pausedSeconds,"Resume restarted or discarded the paused action.");
            pet.PreviewAnimation("Human_Idle");await Task.Delay(300);
            pet.FollowActivityAnimations();pet.CareAction(p=>PetCareService.Start(p,"exercise",DateTimeOffset.UtcNow));
            Check("authored-activity-uses-run",player.Clips[player.ClipIndex].Name=="Human_Run","Exercise still selects the legacy gait.");
            pet.CareAction(PetCareService.Cancel);
            Check("authored-activity-returns-idle",player.Clips[player.ClipIndex].Name=="Human_Idle","Activity exit selected the wrong idle.");
            point=ElementPoint(model);
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Native.RightDown();Native.RightUp();});await Task.Delay(200);
            Check("authored-native-right-click-menu",pet.ContextMenu?.IsOpen==true&&pet.ModelScene is not null,"Right click lost the model or context menu.");
            if(pet.ContextMenu is not null)pet.ContextMenu.IsOpen=false;
            await Task.Delay(300);
            point=ElementPoint(model);
            await InputAsync(()=>
            {
                Native.SetCursorPos(point.X,point.Y);Native.LeftDown();Thread.Sleep(40);Native.LeftUp();
                Thread.Sleep(80);Native.LeftDown();Thread.Sleep(40);Native.LeftUp();
            });await Task.Delay(300);
            var centers=app.Windows.OfType<ControlCenterWindow>().ToArray();
            Check("authored-double-click-single-center",centers.Length==1&&centers[0].IsVisible&&System.Windows.Input.Mouse.Captured is null,"Double click did not open a single responsive control center.");
            var center=centers[0];await FocusWindowAsync(new System.Windows.Interop.WindowInteropHelper(center).Handle);
            var navigation=GetField<System.Windows.Controls.ListBox>(center,"_navigation");
            var settingsItem=(System.Windows.Controls.ListBoxItem)navigation.Items[3];settingsItem.BringIntoView();await Task.Delay(100);
            await ClickAsync(ElementPoint(settingsItem));
            Check("authored-control-center-focus-after-double-click",navigation.SelectedIndex==3&&GetField<System.Windows.Controls.TextBlock>(center,"_pageTitle").Text=="设置","Control center lost input focus with the authored model.");
            center.Close();
            pet.HideToTray();var hiddenFrames=pet.AnimationFrameCount;await Task.Delay(300);
            Check("authored-hidden-clears-native-pointer",!pet.IsInteractionRendering&&GetField<int>(player,"_interactionClip")==-1&&pet.AnimationFrameCount==hiddenFrames,"Hidden window retained pointer/native updates.");
            success=Results.All(r=>(bool)r.GetType().GetProperty("passed")!.GetValue(r)!);
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            try{Native.LeftUp();Native.SetCursorPos(originalCursor.X,originalCursor.Y);foreach(var center in app.Windows.OfType<ControlCenterWindow>().ToArray())center.Close();if(pet is not null)CloseModelPet(pet);}catch{}
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="real-WPF-authored-button-click-drag-and-owner-lifecycle-not-installed-EXE",checks=Results,metrics},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }

    private static void SetField(object target,string name,object value)=>target.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(target,value);
}
