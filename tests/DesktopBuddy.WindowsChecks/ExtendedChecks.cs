using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    private static void RunDataChecks(string temp)
    {
        var profile = Path.Combine(temp, "data-checks");
        var store = new LocalStore(profile);
        Check("default-name-qianmeng", store.LoadSettings().PetName == "浅梦", "Wrong default name.");
        var settingsPath = Path.Combine(profile, "settings.json");
        File.WriteAllText(settingsPath, "{\"PetName\":\"小伴\"}");
        Check("migrate-old-default-name", store.LoadSettings().PetName == "浅梦", "Legacy name did not migrate.");
        File.WriteAllText(settingsPath, "{\"PetName\":\"我的伙伴\"}");
        Check("preserve-custom-name", store.LoadSettings().PetName == "我的伙伴", "Custom name was lost.");
        File.WriteAllText(settingsPath, "not-json");
        store.LoadSettings();
        Check("corrupt-settings-backed-up", Directory.GetFiles(profile, "settings.json.corrupt-*").Length == 1
            && store.RecoveryWarnings.Count == 1, "Corrupt original was not preserved.");
        var newer = "{\"SchemaVersion\":99,\"PetName\":\"future\"}";
        File.WriteAllText(settingsPath, newer);
        var futureStore = new LocalStore(profile);
        var fallback = futureStore.LoadSettings();
        futureStore.SaveSettings(fallback);
        Check("newer-schema-readonly", futureStore.SettingsReadOnly && File.ReadAllText(settingsPath) == newer, "Newer settings overwritten.");
        File.WriteAllText(settingsPath, "broken-original");
        using (var held = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockedStore = new LocalStore(profile);
            var lockedFallback = lockedStore.LoadSettings();
            lockedStore.SaveSettings(lockedFallback);
            Check("failed-backup-protects-original", lockedStore.SettingsReadOnly && File.ReadAllText(settingsPath) == "broken-original",
                "Backup failure allowed overwriting the original.");
        }
        var pet = new PetState { Xp = 95 };
        Check("growth-level-up", pet.AddXp(10) == 1 && pet.Level == 2 && pet.Xp == 5, "Level math incorrect.");
        pet.AddXp(1000); pet.AddXp(-10);
        Check("growth-daily-xp-cap", pet.DailyXp == 25 && pet.Xp == 20, "XP cap bypassed.");
        pet.AddAffection(1000); pet.AddAffection(-10);
        Check("growth-daily-affection-cap", pet.DailyAffection == 20 && pet.Affection == 20, "Affection cap bypassed.");
        pet.DailyXpDate = pet.DailyAffectionDate = "2000-01-01";
        pet.ResetDailyIfNeeded();
        Check("growth-daily-reset", pet.DailyXp == 0 && pet.DailyAffection == 0, "Daily reset failed.");
        var statePath = Path.Combine(profile, "pet-state.json");
        File.WriteAllText(statePath, "{\"SchemaVersion\":99,\"Level\":123}");
        var futurePetStore = new LocalStore(profile);
        futurePetStore.SavePetState(futurePetStore.LoadPetState());
        Check("newer-growth-schema-preserved", File.ReadAllText(statePath).Contains("123"), "Future growth overwritten.");

        var plugins = new PluginService(Path.Combine(temp, "plugins-checks"));
        var source = Path.Combine(temp, "plugin.json");
        string Manifest(string permissions = "pet.say", string line = "测试台词") => JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id = "test.local", name = "本地测试", version = "1.0", permissions = new[] { permissions },
            triggers = new[] { new { id = "first", processNames = new[] { "Code.exe" }, say = line, cooldownMinutes = 60 } }
        });
        File.WriteAllText(source, Manifest());
        var installed = plugins.Import(source);
        Check("plugin-import", plugins.LoadAll().Count == 1, "Plugin import failed.");
        Check("plugin-trigger", plugins.GetTriggeredLine("CODE.EXE") == "测试台词", "Trigger failed.");
        Check("plugin-cooldown", plugins.GetTriggeredLine("code.exe") is null, "Cooldown failed.");
        ExpectRejected("plugin-replace-requires-consent", () => plugins.Import(source));
        File.WriteAllText(source, Manifest(line: "替换台词"));
        installed = plugins.Import(source, true);
        Check("plugin-replace", plugins.LoadAll().Single().Triggers.Single().Say == "替换台词", "Replacement failed.");
        Check("plugin-remove", plugins.Remove(installed) && plugins.LoadAll().Count == 0, "Removal failed.");
        File.WriteAllText(source, Manifest("shell.execute"));
        ExpectRejected("plugin-denies-shell-permission", () => plugins.Import(source));
        File.WriteAllText(source, Manifest().Replace("test.local", "../escape"));
        ExpectRejected("plugin-denies-path-traversal", () => plugins.Import(source));
        ExpectRejected("target-denies-script-url", () => SystemActions.CreateStartInfo(new ApprovedTarget { Kind = "url", Value = "javascript:alert(1)" }));
        ExpectRejected("target-denies-invalid-uwp", () => SystemActions.CreateStartInfo(new ApprovedTarget { Kind = "uwp", Value = "bad!app & calc" }));
        var script = Path.Combine(temp, "fixture.cmd"); File.WriteAllText(script, "exit");
        ExpectRejected("target-denies-cmd", () => SystemActions.CreateStartInfo(new ApprovedTarget { Kind = "path", Value = script }));
        Check("target-uwp-shell-folder", SystemActions.CreateStartInfo(new ApprovedTarget { Kind = "uwp", Value = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" }).FileName.StartsWith("shell:AppsFolder\\"), "UWP launch mapping failed.");
    }

    private static void ExpectRejected(string name, Action action)
    {
        var rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(name, rejected, "Unsafe or unconfirmed input accepted.");
    }

    private static async Task RunExtendedUiChecksAsync(MainWindow pet, ControlCenterWindow center, string temp)
    {
        await FocusWindowAsync(new WindowInteropHelper(center).Handle);
        var nav = GetField<ListBox>(center, "_navigation");
        Check("name-visible-in-panel", center.Title.StartsWith("浅梦") && pet.Settings.PetName == "浅梦", "Rename missing.");
        for (var page = 0; page < 5; page++)
        {
            var item = (ListBoxItem)nav.Items[page];
            await ClickAsync(ElementPoint(item));
            Check("navigation-page-" + page, nav.SelectedIndex == page, "Native navigation click failed.");
            SaveWindowImage(center, Path.ChangeExtension(_report, $".page-{page}.png"));
        }
        center.Width = center.MinWidth; center.Height = center.MinHeight;
        await Task.Delay(150);
        nav.SelectedIndex = 1;
        await Task.Delay(100);
        var input = GetField<TextBox>(center, "_messageInput");
        var send = GetField<Button>(center, "_sendButton");
        Check("chat-input-visible-at-minimum-size", input.ActualWidth > 300 && input.ActualHeight >= 50 && send.IsVisible, "Minimum-size input clipped.");
        SaveWindowImage(center, Path.ChangeExtension(_report, ".minimum.png"));
        Check("chat-keyboard-focus", input.Focus() && input.IsKeyboardFocusWithin, "Chat input cannot receive keyboard focus.");
        input.Text = "你好";
        await (Task)InvokeResult(center, "SendMessageAsync")!;
        Check("offline-chat-roundtrip", GetField<List<ChatLine>>(center, "_history").Count == 2
            && GetField<StackPanel>(center, "_conversation").Children.Count == 3 && send.IsEnabled, "Offline conversation failed.");
        Invoke(center, "ClearConversation");

        using (var server = new LocalChatServer())
        {
            var endpoint = GetField<TextBox>(center, "_endpointInput");
            endpoint.Text = server.Endpoint;
            pet.Settings.RemoteChatConsent = true; pet.Settings.RemoteChatConsentEndpoint = server.Endpoint;
            input.Text = "本地接口测试";
            var sending = (Task)InvokeResult(center, "SendMessageAsync")!;
            var body = await server.Request.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check("chat-busy-disables-send", !send.IsEnabled && Equals(send.Content, "正在回复…"), "Busy feedback missing.");
            using (var json = JsonDocument.Parse(body))
            {
                var messages = json.RootElement.GetProperty("messages");
                Check("chat-request-only-explicit-content", messages.GetArrayLength() == 2
                    && messages[1].GetProperty("content").GetString() == "本地接口测试"
                    && messages[0].GetProperty("content").GetString()!.Contains("浅梦")
                    && !body.Contains("chrome.exe", StringComparison.OrdinalIgnoreCase), "Unexpected context attached to chat.");
            }
            server.Reply.TrySetResult("{\"choices\":[{\"message\":{\"content\":\"本地测试回复\"}}]}");
            await sending.WaitAsync(TimeSpan.FromSeconds(8));
            Check("chat-compatible-api-roundtrip", GetField<List<ChatLine>>(center, "_history").Last().Content == "本地测试回复" && send.IsEnabled, "API response missing.");
        }
        Invoke(center, "ClearConversation");
        using (var server = new LocalChatServer())
        {
            GetField<TextBox>(center, "_endpointInput").Text = server.Endpoint;
            pet.Settings.RemoteChatConsent = true; pet.Settings.RemoteChatConsentEndpoint = server.Endpoint;
            input.Text = "取消测试";
            var sending = (Task)InvokeResult(center, "SendMessageAsync")!;
            await server.Request.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Invoke(center, "ClearConversation");
            await sending.WaitAsync(TimeSpan.FromSeconds(8));
            server.Reply.TrySetResult("{\"choices\":[{\"message\":{\"content\":\"过期回复\"}}]}");
            Check("clear-cancels-pending-reply", GetField<List<ChatLine>>(center, "_history").Count == 0
                && GetField<StackPanel>(center, "_conversation").Children.Count == 1 && send.IsEnabled, "Cancelled response returned to cleared conversation.");
        }
        using (var server = new LocalChatServer())
        {
            GetField<TextBox>(center, "_endpointInput").Text = server.Endpoint;
            pet.Settings.RemoteChatConsent = true; pet.Settings.RemoteChatConsentEndpoint = server.Endpoint;
            input.Text = "关闭取消测试";
            var sending = (Task)InvokeResult(center, "SendMessageAsync")!;
            await server.Request.Task.WaitAsync(TimeSpan.FromSeconds(8));
            center.Close();
            await sending.WaitAsync(TimeSpan.FromSeconds(8));
            Check("close-cancels-pending-reply", GetField<List<ChatLine>>(center, "_history").Count == 1, "Closed panel received a late reply.");
        }
        pet.Settings.ChatEndpoint = "";
        pet.OpenControlCenter();
        center = Application.Current.Windows.OfType<ControlCenterWindow>().Single();
        await Task.Delay(150);
        var centerHandle = new WindowInteropHelper(center).Handle;
        await CancelConfirmationAsync(center, "LockComputer", "确认锁屏");
        await CancelConfirmationAsync(center, "ResetGrowth", "清除成长数据");
        Check("cancel-keeps-growth", pet.Pet.Level >= 1, "Cancel altered growth state.");

        Check("show-hide-hotkey-registered", GetField<bool>(pet, "_showHotkeyRegistered"), "Show hotkey unavailable.");
        await NativeHotkeyAsync(0x20);
        Check("global-hotkey-hides", !pet.IsVisible, "Native global hotkey did not hide pet.");
        await NativeHotkeyAsync(0x20);
        Check("global-hotkey-restores", pet.IsVisible, "Native global hotkey did not restore pet.");
        await NativeHotkeyAsync(0x49);
        Check("global-hotkey-click-through", pet.Settings.ClickThrough, "Interaction hotkey failed.");
        await NativeHotkeyAsync(0x49);
        Check("global-hotkey-restores-interaction", !pet.Settings.ClickThrough, "Interaction hotkey did not reset.");

        var fullscreen = await StartFixtureAsync(true);
        await Task.Delay(2400);
        Check("native-fullscreen-detection", ForegroundMonitor.GetCurrent()?.IsFullscreen == true, "Borderless fullscreen not detected.");
        Check("fullscreen-hides-pet", !pet.IsVisible && GetField<bool>(pet, "_hiddenByFullscreen"), "Fullscreen did not hide pet.");
        fullscreen.Kill(); fullscreen.WaitForExit(3000);
        await FocusWindowAsync(centerHandle);
        await Task.Delay(3300);
        Check("fullscreen-exit-restores-pet", pet.IsVisible, "Leaving fullscreen did not restore pet.");
        pet.HideToTray();
        fullscreen = await StartFixtureAsync(true);
        await Task.Delay(2400);
        fullscreen.Kill(); fullscreen.WaitForExit(3000);
        await FocusWindowAsync(centerHandle);
        await Task.Delay(3300);
        Check("fullscreen-preserves-user-hidden", !pet.IsVisible && GetField<bool>(pet, "_userHidden"), "Fullscreen exit overrode user hiding.");
        pet.RestoreFromTray();
        await CheckExitIpcAsync();
    }

    private static object? InvokeResult(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(target, args);

    private static async Task NativeHotkeyAsync(ushort key)
    {
        await InputAsync(() =>
        {
            Native.KeyInput(0x11, false); Native.KeyInput(0x12, false);
            try { Native.KeyInput(key, false); Thread.Sleep(70); }
            finally { Native.KeyInput(key, true); Native.KeyInput(0x12, true); Native.KeyInput(0x11, true); }
        });
        await Task.Delay(250);
    }

    private static async Task CancelConfirmationAsync(ControlCenterWindow center, string method, string title)
    {
        var dismiss = Task.Run(async () =>
        {
            nint hwnd = 0;
            for (var i = 0; i < 40 && hwnd == 0; i++)
            {
                await Task.Delay(50);
                hwnd = NativeMethods.FindWindow("#32770", title);
            }
            if (hwnd == 0) throw new InvalidOperationException("Confirmation dialog did not appear: " + title);
            NativeMethods.PostMessage(hwnd, 0x0111, 7, 0); // IDNO; do not execute the sensitive operation.
        });
        Invoke(center, method);
        await dismiss;
        Check("confirmation-cancel-" + method, center.IsEnabled, "Cancel did not return control to the panel.");
    }

    private static async Task CheckExitIpcAsync()
    {
        var args = "\"" + typeof(Program).Assembly.Location + "\" --pet-exit-fixture";
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false, CreateNoWindow = true })!;
        Fixtures.Add(process);
        nint hwnd = 0;
        for (var i = 0; i < 50 && hwnd == 0 && !process.HasExited; i++)
        {
            await Task.Delay(100);
            hwnd = NativeMethods.FindWindow(null, "DesktopBuddy pet exit fixture");
        }
        Check("exit-ipc-fixture-starts", hwnd != 0, "Pet subprocess failed to start.");
        NativeMethods.PostMessage(hwnd, NativeMethods.WmAppExit, 0, 0);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
        Check("exit-ipc-graceful-shutdown", process.ExitCode == 0, "Exit IPC did not stop the pet cleanly.");
    }

    private sealed class LocalChatServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public readonly TaskCompletionSource<string> Request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Endpoint { get; }
        public LocalChatServer()
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/chat/completions";
            _ = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                var length = 0;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                // Fixtures use only UTF-8 JSON; read bytes rather than counting decoded characters.
                var builder = new StringBuilder();
                while (Encoding.UTF8.GetByteCount(builder.ToString()) < length)
                {
                    var one = new char[1];
                    if (await reader.ReadAsync(one.AsMemory(), _stop.Token) == 0) break;
                    builder.Append(one[0]);
                }
                Request.TrySetResult(builder.ToString());
                var reply = Encoding.UTF8.GetBytes(await Reply.Task.WaitAsync(_stop.Token));
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {reply.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _stop.Token);
                await stream.WriteAsync(reply, _stop.Token);
            }
            catch (Exception ex) { Request.TrySetException(ex); }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
    }
}
