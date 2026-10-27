using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DesktopBuddy.Services;

public sealed record ChatLine(string Role, string Content);

public sealed class ChatService
{
    private readonly Func<string?> _readApiKey;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(45),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    public ChatService() : this(CredentialStore.Read) { }

    internal ChatService(Func<string?> readApiKey) => _readApiKey = readApiKey;

    public async Task<string> ReplyAsync(string userText, IReadOnlyList<ChatLine> recentHistory, string endpoint, string model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endpoint)) return LocalReply(userText);
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidOperationException("聊天接口必须使用 HTTPS；只有本机地址允许 HTTP。");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("聊天接口地址不能在 URL 中嵌入用户名或密码；请使用 Windows Credential Manager 保存 API Key。");

        var messages = new List<object>
        {
            new { role = "system", content = "你叫浅梦，是一个温和、简洁、尊重边界的 Windows 桌面伙伴。不要催促用户，不对用户的工作作价值判断。你只会收到用户明确输入的聊天内容，不会获得当前前台应用信息。用中文回复，通常控制在 2 至 4 句。" }
        };
        foreach (var line in recentHistory.TakeLast(10))
            if (line.Role is "user" or "assistant") messages.Add(new { role = line.Role, content = line.Content });
        messages.Add(new { role = "user", content = userText });

        var requestBody = JsonSerializer.Serialize(new { model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model.Trim(), messages, temperature = 0.7 });
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        var apiKey = _readApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"聊天服务返回 {(int)response.StatusCode}。请检查接口地址、模型和 API Key。\n{Trim(body, 300)}");

        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content) ? "我暂时没有组织好回答，再试一次好吗？" : Trim(content, 4000);
    }

    private static string LocalReply(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Contains("你好", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("嗨", StringComparison.OrdinalIgnoreCase))
            return "你好呀，我在这里陪你。现在是离线陪伴模式；如果你配置兼容接口，我也可以接入你选择的模型。";
        if (trimmed.Contains("累", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("休息", StringComparison.OrdinalIgnoreCase))
            return "辛苦了。你可以先离开屏幕一会儿，等准备好了再继续，我会安静等你。";
        if (trimmed.Contains("笑话", StringComparison.OrdinalIgnoreCase))
            return "为什么程序员喜欢黑夜？因为白天的 bug 太显眼了。";
        return $"我听到了：“{Trim(trimmed, 160)}”。我目前使用本地离线回应，还没有连接语言模型。";
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
