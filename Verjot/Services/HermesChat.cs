using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>
/// One question to the user's Hermes agent, over the OpenAI-compatible chat-completions shape:
/// POST {url} with {"model": …, "messages": […]}, the answer in choices[0].message.content.
/// No streaming yet (a plain request is enough for short questions).
/// </summary>
internal static class HermesChat
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static string Ask(Settings settings, IReadOnlyList<(string Role, string Text)> history)
    {
        string url = settings.HermesUrl.Trim();
        if (url.Length == 0) throw new InvalidOperationException("no endpoint");

        var messages = new List<object>();
        foreach (var (role, text) in history)
            messages.Add(new { role, content = text });

        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["stream"] = false,
        };
        if (settings.HermesModel.Trim().Length > 0) body["model"] = settings.HermesModel.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (settings.HermesKey.Trim().Length > 0)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.HermesKey.Trim());

        using var response = Http.SendAsync(request).GetAwaiter().GetResult();
        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new IOException($"{(int)response.StatusCode} {response.StatusCode}: {(json.Length > 300 ? json[..300] + "…" : json)}");

        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return (content ?? "").Trim();
    }
}
