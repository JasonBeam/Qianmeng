using System.Reflection;

namespace DesktopBuddy.Services;

public static class SapiSpeechService
{
    public static Task SpeakAsync(string text)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? voice = null;
            try
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: true)!;
                voice = Activator.CreateInstance(type);
                type.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [text, 0]);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally
            {
                if (voice is not null && System.Runtime.InteropServices.Marshal.IsComObject(voice))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice);
            }
        }) { IsBackground = true, Name = "DesktopBuddy-SAPI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
