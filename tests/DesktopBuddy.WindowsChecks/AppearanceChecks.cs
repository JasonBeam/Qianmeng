using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopBuddy;
using DesktopBuddy.ModelCore;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;

internal static partial class Program
{
    private static async Task RunAppearanceChecksAsync(Application app)
    {
        var temp=Path.Combine(Path.GetTempPath(),"Qianmeng-Appearance-"+Guid.NewGuid().ToString("N"));
        MainWindow? pet=null;var success=false;var metrics=new List<object>();
        async Task Loaded()
        {
            for(var i=0;i<150&&(pet!.IsModelLoading||pet.ModelScene is null);i++)await Task.Delay(100);
            Check("default-person-loaded",pet!.ModelScene is not null&&!pet.IsModelLoading,"The packaged default person did not load.");
        }
        try
        {
            var bundle=Path.Combine(AppContext.BaseDirectory,"models","default-companion.glb");
            Check("default-person-bundled",File.Exists(bundle),"Private build is missing its local default GLB.");
            var store=new LocalStore(temp);
            pet=new MainWindow(store,_=>{},new ChatService(()=>null)){Topmost=false,ShowActivated=false};
            app.MainWindow=pet;pet.Show();await Task.Delay(100);await Loaded();
            Check("default-model-is-skeletal",pet.AnimationNames.Length>0,"Default person lost its animations.");
            Check("default-person-hides-fallback",((FrameworkElement)pet.FindName("PetModel")).Visibility==Visibility.Visible&&((FrameworkElement)pet.FindName("PetEmoji")).Visibility==Visibility.Collapsed,"Fallback is still displayed over the person.");
            var player=GetField<object>(pet,"_animationPlayer");
            var sampler=GetField<RigSampler>(player,"_sampler");
            pet.PreviewAnimation(pet.AnimationNames.First(n=>n.Contains("Idle")));
            pet.PauseAnimation();
            var before=sampler.Positions.Select(m=>(Vector3[])m.Clone()).ToArray();
            player.GetType().GetMethod("Select")!.Invoke(player,new object[]{Array.FindIndex(pet.AnimationNames,n=>n.Contains("Running"))});
            var difference=sampler.Positions.SelectMany((mesh,m)=>mesh.Select((p,v)=>Vector3.Distance(p,before[m][v]))).Max();
            Check("paused-selection-updates-visible-pose",difference>.01,"Paused clip selection kept the previous skeleton.");
            pet.PreviewAnimation(pet.AnimationNames.First(n=>n.Contains("Idle")));
            // Let the selected clip's visible transition finish before freezing caption evidence.
            // Pausing immediately can correctly retain the outgoing Running pose at blend progress zero.
            await Task.Delay(400);
            pet.PauseAnimation();
            var root=(Grid)pet.FindName("RootGrid");var background=root.Background;
            var caption=(Border)pet.FindName("PetCaption");
            var captionColor=((SolidColorBrush)caption.Background).Color;
            Check("caption-has-background-independent-backplate",captionColor.A>=230&&captionColor.R<40&&captionColor.G<50&&captionColor.B<65,"Caption backdrop cannot guarantee contrast on a light desktop.");
            foreach(var entry in new[]{("light",Color.FromRgb(248,249,252)),("dark",Color.FromRgb(17,22,32))})
            {
                root.Background=new SolidColorBrush(entry.Item2);pet.UpdateLayout();
                SaveWindowImage(pet,Path.ChangeExtension(_report,"."+entry.Item1+".png"));
            }
            root.Background=background;
            var nameLabel=(TextBlock)pet.FindName("PetNameText");var statusLabel=(TextBlock)pet.FindName("PetStatusText");
            var originalName=nameLabel.Text;var originalStatus=statusLabel.Text;
            pet.SetPetSize(.6);nameLabel.Text="浅梦的一个很长的自定义名称用于边界检查";statusLabel.Text="这是一条需要换行显示的较长状态文字";
            root.Background=Brushes.White;pet.UpdateLayout();
            var captionBounds=caption.TransformToAncestor(root).TransformBounds(new Rect(0,0,caption.ActualWidth,caption.ActualHeight));
            Check("small-caption-fits-window",captionBounds.Left>=-1&&captionBounds.Right<=root.ActualWidth+1&&captionBounds.Top>=-1&&captionBounds.Bottom<=root.ActualHeight+1,"Caption clips at the minimum pet size.");
            SaveWindowImage(pet,Path.ChangeExtension(_report,".small-long-caption.png"));
            nameLabel.Text=originalName;statusLabel.Text=originalStatus;root.Background=background;pet.SetPetSize(1);
            await pet.ImportPetModelAsync(bundle);
            Check("custom-selection-still-supported",!string.IsNullOrWhiteSpace(pet.Settings.PetModelFile),"Import no longer saves a custom selection.");
            pet.ResetPetModel();await Loaded();
            Check("reset-selects-default-person",pet.Settings.PetModelFile==""&&pet.ModelScene is not null,"Reset returned to the old fallback.");
            var pending=pet.ImportPetModelAsync(bundle);pet.ResetPetModel();await pending;await Loaded();
            Check("default-person-wins-import-cancellation",pet.Settings.PetModelFile==""&&pet.ModelScene is not null,"A late import replaced default selection.");
            // Restart from this isolated profile; never change the real user's profile.
            CloseModelPet(pet);pet=new MainWindow(new LocalStore(temp),_=>{},new ChatService(()=>null)){Topmost=false,ShowActivated=false};
            app.MainWindow=pet;pet.Show();await Task.Delay(100);await Loaded();
            Check("restart-keeps-default-person",pet.Settings.PetModelFile==""&&pet.ModelScene is not null,"Restart did not restore default person.");
            pet.FollowActivityAnimations();
            await Task.Delay(5000);
            var process=Process.GetCurrentProcess();process.Refresh();var cpu=process.TotalProcessorTime;var clock=Stopwatch.StartNew();var framesBefore=pet.AnimationFrameCount;
            await Task.Delay(10000);process.Refresh();
            metrics.Add(new{phase="default-person-idle-no-forced-GC",privateMiB=process.PrivateMemorySize64/1048576d,workingSetMiB=process.WorkingSet64/1048576d,oneCoreCpuPercent=100*(process.TotalProcessorTime-cpu).TotalSeconds/clock.Elapsed.TotalSeconds,fps=(pet.AnimationFrameCount-framesBefore)/clock.Elapsed.TotalSeconds,pet.IsInteractionRendering,pet.AnimationSummary});
            success=true;
        }
        catch(Exception ex){Results.Add(new{name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            if(pet is not null)CloseModelPet(pet);
            File.WriteAllText(_report,JsonSerializer.Serialize(new{success,mode="isolated-WPF-default-and-caption",checks=Results,metrics},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
}
