using System.IO;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DesktopBuddy.Models;
using DesktopBuddy.Services;

namespace DesktopBuddy.WindowsChecks;
internal static partial class Program
{
    private static async Task RunCareChecksAsync(Application app)
    {
        var temp = Path.Combine(Path.GetTempPath(), "Qianmeng-Care-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp); MainWindow? pet = null; ControlCenterWindow? center = null;
        var success = false;
        try
        {
            var now = DateTimeOffset.UtcNow; var p = new PetState();
            Check("care-starter-coins", p.Coins == 60, "Starter balance wrong.");
            PetCareService.Buy(p, "rice"); Check("purchase-debits-and-inventories", p.Coins == 48 && p.Inventory["rice"] == 1, "Purchase mismatch.");
            p.Fullness = 50; PetCareService.Consume(p, "rice"); Check("feeding-consumes-owned-item", p.Fullness == 75 && !p.Inventory.ContainsKey("rice"), "Feeding not transactional.");
            ExpectRejected("no-free-feeding", () => PetCareService.Consume(p, "rice"));
            p.Coins = 0; ExpectRejected("cannot-buy-without-coins", () => PetCareService.Buy(p, "water"));
            p.Coins = 100; PetCareService.Buy(p, "mint");
            Check("permanent-skin-purchased", p.OwnedSkins.Contains("mint") && p.Coins == 0, "Skin missing.");
            p.Coins = 200; ExpectRejected("no-duplicate-skin-charge", () => PetCareService.Buy(p, "mint"));
            Check("duplicate-skin-keeps-money", p.Coins == 200, "Duplicate charged.");
            p.Fullness = 100; p.Hydration = 100; PetCareService.Buy(p, "water");
            ExpectRejected("full-state-does-not-waste-water", () => PetCareService.Consume(p, "water"));
            Check("full-state-preserves-inventory", p.Inventory["water"] == 1, "Wasted full-state item.");
            p.Hydration = 80; PetCareService.Consume(p, "water"); Check("water-clamps-maximum", p.Hydration == 100, "Water overflow.");
            p.Energy = 80; var coins = p.Coins; PetCareService.Start(p, "work", now);
            Check("work-charges-energy-upfront", p.Energy == 65 && p.Coins == coins, "Premature rewards.");
            ExpectRejected("mutually-exclusive-activities", () => PetCareService.Start(p, "study", now));
            PetCareService.Advance(p, now.AddMinutes(4)); Check("no-premature-work-reward", p.Coins == coins, "Early reward.");
            PetCareService.Advance(p, now.AddMinutes(5)); Check("work-pays-on-completion", p.Coins == coins + 30 && p.Activity == "", "Work not settled.");
            PetCareService.Advance(p, now.AddMinutes(5)); Check("no-double-reward", p.Coins == coins + 30, "Double credit.");
            PetCareService.Start(p, "study", now); PetCareService.Advance(p, now.AddMinutes(3));
            Check("study-earns-skill", p.SkillPoints == 1, "No skill reward.");
            var xp = p.Xp; PetCareService.SpendSkill(p); Check("skill-converts-to-xp", p.SkillPoints == 0 && p.Xp == xp + 15, "XP conversion wrong.");
            ExpectRejected("cannot-overspend-skill", () => PetCareService.SpendSkill(p));
            p.Energy = 80; PetCareService.Start(p, "exercise", now); PetCareService.Advance(p, now.AddMinutes(4));
            Check("exercise-increases-health-cap", p.MaxHealth == 101 && p.Fitness == 1, "Exercise reward wrong.");
            p = new PetState { MaxHealth = 150, Health = 149, Fitness = 100 }; PetCareService.Start(p, "exercise", now); PetCareService.Advance(p, now.AddMinutes(4));
            Check("training-has-caps", p.MaxHealth == 150 && p.Health == 150 && p.Fitness == 100, "Training exceeds caps.");
            p = new PetState { Energy = 10, Mood = 30 }; PetCareService.Start(p, "play", now); PetCareService.Advance(p, now.AddMinutes(2));
            Check("playing-costs-energy-improves-mood", p.Energy == 0 && p.Mood == 48, "Play wrong.");
            ExpectRejected("exhausted-cannot-work", () => PetCareService.Start(p, "work", now));
            PetCareService.Start(p, "sleep", now); ExpectRejected("cannot-feed-during-sleep", () => { p.Inventory["rice"] = 1; PetCareService.Consume(p, "rice"); });
            PetCareService.Advance(p, now.AddMinutes(5)); Check("sleep-restores-energy", p.Energy == 40, "Sleep wrong.");
            p = new PetState { Energy = 0, Fullness = 0, Hydration = 0, Health = 0, Coins = 0, LastCareUtc = now };
            PetCareService.Start(p, "sleep", now); PetCareService.Advance(p, now.AddMinutes(5));
            PetCareService.Start(p, "work", now.AddMinutes(5)); PetCareService.Advance(p, now.AddMinutes(10));
            PetCareService.Buy(p,"rice"); PetCareService.Buy(p,"water");
            Check("empty-wallet-low-stats-recoverable", p.Inventory.ContainsKey("rice") && p.Inventory.ContainsKey("water"), "Economy soft-lock.");
            p = new PetState { Energy = 100, LastCareUtc = now }; ExpectRejected("full-energy-rejects-sleep", () => PetCareService.Start(p, "sleep", now));
            PetCareService.Start(p, "work", now); var paid = p.Coins; PetCareService.Cancel(p); PetCareService.Advance(p, now.AddMinutes(6));
            Check("cancel-has-no-reward-or-refund", p.Coins == paid && p.Energy == 85, "Cancel exploit.");
            var untrained = new PetState { LastCareUtc = now }; var trained = new PetState { LastCareUtc = now, Fitness = 100 };
            PetCareService.Advance(untrained, now.AddHours(1)); PetCareService.Advance(trained, now.AddHours(1));
            Check("fitness-reduces-natural-decay", trained.Fullness > untrained.Fullness && trained.Hydration > untrained.Hydration && trained.Energy > untrained.Energy, "Fitness ineffective.");
            p = new PetState { LastCareUtc = now }; PetCareService.Advance(p, now.AddDays(2));
            Check("offline-loss-capped-one-hour", p.Fullness == 68 && p.Hydration == 62 && p.Energy == 74, "Unbounded offline loss.");
            var dir = Path.Combine(temp, "profile"); var store = new LocalStore(dir);
            File.WriteAllText(Path.Combine(dir, "pet-state.json"), "{\"SchemaVersion\":1,\"Level\":8,\"Xp\":20,\"Mood\":0,\"Affection\":123}");
            var migrated = store.LoadPetState(); Check("legacy-growth-migrates", migrated.SchemaVersion == 2 && migrated.Level == 8 && migrated.Xp == 20 && migrated.Affection == 123 && migrated.Mood == 50 && migrated.Coins == 60, "Legacy data lost.");
            PetCareService.Start(migrated, "work", now); store.SavePetState(migrated); var loaded = store.LoadPetState(); PetCareService.Advance(loaded, now.AddMinutes(5)); store.SavePetState(loaded);
            var restored = store.LoadPetState(); PetCareService.Advance(restored, now.AddMinutes(5)); Check("restart-rewards-exactly-once", restored.Coins == 90 && restored.Activity == "", "Restart duplications.");
            File.WriteAllText(Path.Combine(dir, "pet-state.json"), "{\"SchemaVersion\":2,\"Coins\":-99,\"Inventory\":{\"unknown\":4,\"rice\":99999},\"OwnedSkins\":null,\"Activity\":\"hack\"}");
            var sanitized = store.LoadPetState(); Check("invalid-care-data-sanitized", sanitized.Coins == 0 && sanitized.Inventory.Count == 1 && sanitized.Inventory["rice"] == 999 && sanitized.OwnedSkins.Contains("blue") && sanitized.Activity == "", "Invalid data accepted.");
            File.Delete(Path.Combine(dir, "pet-state.json"));
            pet = new MainWindow(new LocalStore(dir), _=>{}, new ChatService(()=>null)); pet.Settings.AutoHideFullscreen = false;
            pet.Show(); pet.OpenControlCenter(); await Task.Delay(400); center = app.Windows.OfType<ControlCenterWindow>().Single();
            var nav = GetField<ListBox>(center, "_navigation");
            Check("shop-navigation-present", nav.Items.Count == 7, "Shop not added.");
            Check("six-state-bars", GetField<Dictionary<string, (ProgressBar Bar, TextBlock Value)>>(center,"_careBars").Count == 6, "State bars missing.");
            Check("no-free-feed-controls", !Descendants<Button>(center).Any(x=>(x.Content?.ToString()??"").Contains("饱食度 +20")), "Free feeding remains.");
            nav.SelectedIndex = 6; await Task.Delay(150);
            var buy = Descendants<Button>(center).Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "购买暖心饭团");
            await FocusWindowAsync(new WindowInteropHelper(center).Handle); await ClickAsync(ElementPoint(buy));
            Check("native-shop-purchase", pet.Pet.Coins == 48 && pet.Pet.Inventory.GetValueOrDefault("rice") == 1, "Native purchase failed.");
            SaveWindowImage(center, Path.ChangeExtension(_report,".shop.png"));
            nav.SelectedIndex = 0; await Task.Delay(100);
            var feed = Descendants<Button>(center).Single(x=>(x.Content?.ToString()??"").StartsWith("使用 暖心饭团")); feed.BringIntoView(); await Task.Delay(150); await ClickAsync(ElementPoint(feed));
            Check("native-backpack-consume", pet.Pet.Fullness == 100 && pet.Pet.Inventory.GetValueOrDefault("rice") == 0, "Native feeding failed.");
            var sleep = Descendants<Button>(center).Single(x=>Equals(x.Content,"睡觉 · 5 分钟")); sleep.BringIntoView(); await Task.Delay(100); await ClickAsync(ElementPoint(sleep));
            Check("native-activity-start", pet.Pet.Activity == "sleep", "Activity button failed.");
            var cancel = Descendants<Button>(center).Single(x=>Equals(x.Content,"提前结束")); await ClickAsync(ElementPoint(cancel));
            Check("native-activity-cancel", pet.Pet.Activity == "", "Cancel button failed.");
            var overviewScroll = Descendants<ScrollViewer>(center).First(); overviewScroll.ScrollToTop(); await Task.Delay(100); SaveWindowImage(center, Path.ChangeExtension(_report,".overview.png"));
            nav.SelectedIndex = 1; await Task.Delay(100); var input = GetField<TextBox>(center,"_messageInput"); input.Focus();
            await InputAsync(()=>Native.TextInput("回车发送测试")); await InputAsync(()=>{Native.KeyInput(0x0D,false);Native.KeyInput(0x0D,true);}); await Task.Delay(400);
            Check("native-enter-sends", input.Text == "" && GetField<List<ChatLine>>(center,"_history").Count == 2,"Enter did not send.");
            input.Focus(); await InputAsync(()=>Native.TextInput("第一行"));
            await InputAsync(()=>{ Native.KeyInput(0x10,false); Native.KeyInput(0x0D,false); Native.KeyInput(0x0D,true); Native.KeyInput(0x10,true); }); await Task.Delay(100);
            Check("native-shift-enter-newline", input.Text.Contains('\n') && GetField<List<ChatLine>>(center,"_history").Count == 2, "Shift Enter sent.");
            var ime = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input),0,Key.ImeProcessed) { RoutedEvent=Keyboard.PreviewKeyDownEvent };
            input.RaiseEvent(ime); Check("ime-composition-not-sent", !ime.Handled && GetField<List<ChatLine>>(center,"_history").Count == 2,"IME swallowed.");
            nav.SelectedIndex = 3; await Task.Delay(100);
            var slider = Descendants<Slider>(center).Single(); slider.Value=1.4;
            var apply = Descendants<Button>(center).Single(x=>Equals(x.Content,"应用大小")); await ClickAsync(ElementPoint(apply)); await Task.Delay(100);
            Check("native-size-setting", Math.Abs(pet.Width-266)<1 && Math.Abs(pet.Store.LoadSettings().PetSize-1.4)<0.001,"Size not persisted.");
            pet.SetPetSize(0.6); Check("size-small", Math.Abs(pet.Width-114)<1,"Small setting wrong.");
            pet.SetPetSize(1.8); Check("size-large", Math.Abs(pet.Width-342)<1,"Large setting wrong.");
            pet.SetPetSize(1); Check("size-restores", Math.Abs(pet.Width-190)<1,"Default setting wrong.");
            SaveWindowImage(center, Path.ChangeExtension(_report,".settings.png"));
            pet.CareAction(q=>{q.Coins=200;return "fixture";}); pet.CareAction(q=>PetCareService.Buy(q,"plum")); pet.ChooseDefaultSkin("plum");
            Check("appearance-persists", pet.Store.LoadPetState().Skin=="plum" && pet.Store.LoadSettings().UseDefaultAppearance,"Skin setting lost.");
            SaveWindowImage(pet, Path.ChangeExtension(_report,".appearance.png"));
            var money=pet.Pet.Coins; var stateFile=Path.Combine(dir,"pet-state.json");
            using(var lockedFile=new FileStream(stateFile, FileMode.Open,FileAccess.Read,FileShare.None))
                ExpectRejectedIo("save-failure-no-live-charge",()=>pet.CareAction(q=>PetCareService.Buy(q,"rice")));
            Check("failed-write-preserves-money",pet.Pet.Coins==money,"In-memory money mutated on failed write.");
            center.Close();center=null; CloseModelPet(pet); pet=null;
            var futureDir=Path.Combine(temp,"future");Directory.CreateDirectory(futureDir);File.WriteAllText(Path.Combine(futureDir,"pet-state.json"),"{\"SchemaVersion\":99,\"Coins\":999}");
            pet=new MainWindow(new LocalStore(futureDir),_=>{},new ChatService(()=>null));
            ExpectRejected("future-schema-cannot-charge",()=>pet.CareAction(q=>PetCareService.Buy(q,"rice")));
            Check("future-schema-file-preserved",File.ReadAllText(Path.Combine(futureDir,"pet-state.json")).Contains("999"),"Future file overwritten.");
            success=true;
        }
        catch(Exception ex) {Results.Add(new {name="failure",passed=false,detail=ex.ToString()});}
        finally
        {
            try {center?.Close();if(pet is not null)CloseModelPet(pet);Native.LeftUp();Mouse.Capture(null);}catch{}
            try{Directory.Delete(temp,true);}catch{}
            File.WriteAllText(_report,JsonSerializer.Serialize(new {success,finishedAt=DateTimeOffset.Now,windows=Environment.OSVersion.VersionString,checks=Results},new JsonSerializerOptions{WriteIndented=true}));
            app.Shutdown(success?0:1);
        }
    }
    private static void ExpectRejectedIo(string name,Action action)
    { var rejected=false; try{action();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){rejected=true;} Check(name,rejected,"Expected IO failure not observed."); }
}
