using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileXfer;

// This is an app-specific transport, not a browser fetch polyfill. Only these
// existing FileXfer routes are reachable, at the server owned by this process.
internal sealed class ApiBridge : IDisposable
{
    private readonly HttpClient client;
    private readonly Dictionary<int, Task<string>> jobs = new();
    private int nextId;
    internal Func<JsonElement, string>? NativeCall;
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
        { "/api/status", "/api/folder", "/api/remotes", "/api/remote-entry" };
    private static readonly HashSet<string> Writes = new(StringComparer.Ordinal)
        { "/api/folder", "/api/pick-folder", "/api/pick-files",
          "/api/remotes/command", "/api/optical/start", "/api/optical/stop" };

    internal ApiBridge(int port)
    {
        client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(10) };
    }

    public string Call(string input)
    {
        try
        {
            if (input.Length > 65536) throw new ArgumentException("要求が大きすぎます。");
            using var json = JsonDocument.Parse(input);
            var root = json.RootElement;
            if (root.TryGetProperty("native", out _)) return NativeCall?.Invoke(root) ?? throw new InvalidOperationException("Native optical component unavailable");
            if (root.TryGetProperty("poll", out var poll))
            {
                int id = poll.GetInt32();
                if (!jobs.TryGetValue(id, out var job)) throw new ArgumentException("不明な要求です。");
                if (!job.IsCompleted) return "{\"pending\":true}";
                jobs.Remove(id);
                return job.GetAwaiter().GetResult();
            }
            if (jobs.Count >= 16) throw new InvalidOperationException("処理中の要求が多すぎます。");
            string path = root.GetProperty("path").GetString() ?? "";
            string method = root.TryGetProperty("body", out var body) ? "POST" : "GET";
            string route = path.Split('?')[0];
            if (!(method == "GET" ? Reads : Writes).Contains(route) || path.Contains('#') || path.Contains('\\'))
                throw new ArgumentException("許可されていないFileXfer APIです。");
            int jobId = ++nextId;
            jobs.Add(jobId, Send(method, path, method == "POST" ? body.GetRawText() : null));
            return JsonSerializer.Serialize(new { job = jobId });
        }
        catch (Exception e) { return JsonSerializer.Serialize(new { error = e.Message }); }
    }

    private async Task<string> Send(string method, string path, string? body)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var json = JsonDocument.Parse(text);
            if (!response.IsSuccessStatusCode)
                return JsonSerializer.Serialize(new { error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : $"HTTP {(int)response.StatusCode}" });
            return "{\"result\":" + text + "}";
        }
        catch (Exception e) { return JsonSerializer.Serialize(new { error = e.Message }); }
    }

    public void Dispose() { client.CancelPendingRequests(); client.Dispose(); }
}
