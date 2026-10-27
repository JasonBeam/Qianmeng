using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static async Task RunPublishedMotionChecksAsync(Application app,string[] args)
    {
        Process? product=null;var success=false;var evidence=new List<object>();Native.GetCursorPos(out var cursor);
        var userProfile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopBuddy");
        Dictionary<string,string> UserHashes()=>Directory.Exists(userProfile)?Directory.GetFiles(userProfile,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))):new();
        Dictionary<string,string>? before=null;
        try
        {
            string Arg(string key)=>args[Array.IndexOf(args,key)+1];
            foreach(var key in new[]{"--package-executable","--motion-profile-dir"})
                if(Array.IndexOf(args,key)<0)throw new ArgumentException(key+" required.");
            var exe=Path.GetFullPath(Arg("--package-executable"));var profile=Path.GetFullPath(Arg("--motion-profile-dir"));
            var repositoryRoot=AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(repositoryRoot,"DesktopBuddy.sln"))&&Path.GetDirectoryName(repositoryRoot) is { } parent)repositoryRoot=parent;
            var workRoot=Path.GetFullPath(Path.Combine(repositoryRoot,"work")+Path.DirectorySeparatorChar);
            if(!File.Exists(Path.Combine(repositoryRoot,"DesktopBuddy.sln"))||!profile.StartsWith(workRoot,StringComparison.OrdinalIgnoreCase))throw new IOException("Acceptance profile must stay under the repository work directory.");
            if(Directory.Exists(profile)||File.Exists(profile))throw new IOException("Refuse existing acceptance profile.");
            if(Process.GetProcessesByName("DesktopBuddy").Length!=0)throw new IOException("Existing DesktopBuddy instance; refusing interference.");
            before=UserHashes();
            var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true};
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]=Path.Combine(profile,"bundle-cache");
            start.Environment["TEMP"]=Path.Combine(Path.GetDirectoryName(profile)!,"windows-checks-temp");start.Environment["TMP"]=start.Environment["TEMP"];
            start.ArgumentList.Add("--acceptance-profile");start.ArgumentList.Add(profile);start.ArgumentList.Add("--diagnose-startup");
            product=Process.Start(start)!;
            for(var i=0;i<120&&(NativeMethods.FindPetWindow()==0||!File.Exists(Path.Combine(profile,"logs","startup-state.json")));i++)await Task.Delay(100);
            var pet=NativeMethods.FindPetWindow();NativeMethods.GetWindowThreadProcessId(pet,out var owner);
            Check("published-owned-pet-window",pet!=0&&owner==product.Id&&!product.HasExited,"Window is not the owned payload process.");
            using(var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(profile,"logs","startup-state.json"))))
            {
                var root=state.RootElement;
                Check("published-default-girl-visible",root.GetProperty("hasModel").GetBoolean()&&root.GetProperty("modelVisibility").GetString()=="Visible"&&root.GetProperty("emojiVisibility").GetString()=="Collapsed","Default girl/fallback state invalid.");
                Check("published-default-eight-clips",root.GetProperty("animationClips").GetInt32()==8,"Payload lost candidate clips.");
                Check("published-default-animation-advances",root.GetProperty("renderedAnimationFrames").GetInt64()>3,"Payload animation did not advance.");
            }
            CaptureNativeWindow(pet,Path.ChangeExtension(_report,".default.png"));
            var requested=Array.IndexOf(args,"--published-actions");
            var definitions=requested>=0?Arg("--published-actions").Split(',').Select(id=>CompanionActionMotion.Library.Single(d=>d.Id==id)).ToArray():
                new[]{CompanionActionKind.Waving,CompanionActionKind.Running,CompanionActionKind.Curtsy,CompanionActionKind.Squatting,CompanionActionKind.ConfidentPose,CompanionActionKind.HeadScratching,CompanionActionKind.Hugging}.Select(kind=>CompanionActionMotion.Library.Single(d=>d.Kind==kind)).ToArray();
            foreach(var definition in definitions)
            {
                var kind=definition.Kind;var actionClock=Stopwatch.StartNew();
                var centerHandle=await RestoreControlCenterAsync(pet);var center=AutomationElement.FromHandle(centerHandle);
                void SelectModels()
                {
                    var item=FindUi(center,ControlType.ListItem,"模型");
                    if(!item.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var selection))throw new InvalidOperationException("Model navigation lacks selection pattern.");
                    ((SelectionItemPattern)selection).Select();
                }
                SelectModels();await Task.Delay(250);
                var button=FindUi(center,ControlType.Button,definition.Label);
                Check("published-action-invokable-"+definition.Id,button.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke),"Action does not expose Invoke.");
                ((InvokePattern)invoke).Invoke();
                bool ActiveLabel()=>center.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().Any(e=>e.Current.Name.Contains(" · "+definition.Label)&&e.Current.Name.Contains("播放中"));
                var started=ActiveLabel();for(var wait=0;wait<12&&!started;wait++){await Task.Delay(30);started=ActiveLabel();}
                Check("published-action-starts-"+definition.Id,started,"Invoked action never appeared in actual playback status.");
                await CloseControlCenterAsync();
                await Task.Delay(kind==CompanionActionKind.Waving?1000:Math.Max(250,(int)(definition.DurationSeconds*450)));
                var image=Path.ChangeExtension(_report,"."+definition.Id+".png");CaptureNativeWindow(pet,image);
                Check("published-action-keeps-window-"+definition.Id,!product.HasExited&&NativeMethods.IsWindowVisible(pet),"Action hid or crashed the payload.");
                var duration=kind==CompanionActionKind.Waving?3.3334:kind==CompanionActionKind.Running?.8:definition.DurationSeconds;
                await Task.Delay((int)(duration*1000)+400);
                center=AutomationElement.FromHandle(await RestoreControlCenterAsync(pet));
                var texts=center.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().Select(e=>e.Current.Name).ToArray();
                var selected=center.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Cast<AutomationElement>()
                    .Where(e=>e.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var pattern)&&((SelectionItemPattern)pattern).Current.IsSelected).Select(e=>e.Current.Name).ToArray();
                var returnImage=Path.ChangeExtension(_report,"."+definition.Id+".return-panel.png");CaptureNativeWindow(new nint(center.Current.NativeWindowHandle),returnImage);
                evidence.Add(new{phase="return-before-assertion",definition.Id,elapsedSeconds=actionClock.Elapsed.TotalSeconds,selectedNavigation=selected,texts,returnImage,petVisible=NativeMethods.IsWindowVisible(pet),foreground=NativeMethods.GetForegroundWindow().ToInt64()});
                SelectModels();await Task.Delay(200);
                texts=center.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().Select(e=>e.Current.Name).ToArray();
                evidence.Add(new{phase="return-model-page",definition.Id,elapsedSeconds=actionClock.Elapsed.TotalSeconds,texts});
                Check("published-action-returns-idle-"+definition.Id,texts.Any(t=>t.Contains("Human_Idle")&&t.Contains("播放中")&&!t.Contains(" · "+definition.Label)),"Action did not return to idle status without an active action label.");
                evidence.Add(new{definition.Id,definition.Label,image,duration,invocation="UIAutomation InvokePattern on real EXE button; not a native mouse click"});
                await CloseControlCenterAsync();
            }
            var point=PackagePetPoint(pet);NativeMethods.GetWindowRect(pet,out var rect);
            await InputAsync(()=>{Native.SetCursorPos(point.X,point.Y);Native.LeftDown();try{Thread.Sleep(100);for(var i=1;i<=6;i++){Native.SetCursorPos(point.X-i*18,point.Y-i*6);Thread.Sleep(70);}}finally{Native.LeftUp();}});
            await Task.Delay(500);NativeMethods.GetWindowRect(pet,out var after);
            Check("published-native-model-drag",Math.Abs(after.Left-rect.Left)>40,"Actual payload drag did not move pet.");
            CaptureNativeWindow(pet,Path.ChangeExtension(_report,".after-drag.png"));
            NativeMethods.PostMessage(pet,NativeMethods.WmAppExit,0,0);await product.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check("published-graceful-exit",product.ExitCode==0,"Payload exit failed.");
            Check("published-isolated-profile-saved",File.Exists(Path.Combine(profile,"settings.json"))&&File.Exists(Path.Combine(profile,"pet-state.json")),"Isolated payload did not persist.");
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            try{if(product is not null&&!product.HasExited){var hwnd=NativeMethods.FindPetWindow();NativeMethods.GetWindowThreadProcessId(hwnd,out var owner);if(owner==product.Id)NativeMethods.PostMessage(hwnd,NativeMethods.WmAppExit,0,0);await product.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));}}catch{}
            if(product is not null&&!product.HasExited){try{product.Kill();await product.WaitForExitAsync();Results.Add(new{name="published-required-owned-process-termination",passed=false});success=false;}catch{}}
            if(before is not null)
            {
                try{var afterHashes=UserHashes();var preserved=before.Count==afterHashes.Count&&before.All(p=>afterHashes.TryGetValue(p.Key,out var hash)&&hash==p.Value);Results.Add(new{name="published-real-user-profile-unchanged",passed=preserved});if(!preserved)success=false;}
                catch(Exception ex){Results.Add(new{name="published-user-profile-audit",passed=false,detail=ex.Message});success=false;}
            }
            product?.Dispose();Native.SetCursorPos(cursor.X,cursor.Y);
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="actual-self-contained-published-EXE-motion-only-isolated-profile-not-NSIS-lifecycle-resource-gate-deferred-by-user",checks=Results,evidence},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
}
