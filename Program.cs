using Anthropic;
using Anthropic.Models.Messages;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<AnthropicClient>(_ =>
    new AnthropicClient { ApiKey = builder.Configuration["Anthropic:ApiKey"] });

var app = builder.Build();

// IIS 서브경로 배포를 위한 PathBase 설정
app.UsePathBase("/claudechatv1");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

// SSE streaming endpoint
app.MapPost("/api/chat", async (HttpContext context, AnthropicClient client) =>
{
    var body = await JsonSerializer.DeserializeAsync<ChatRequest>(
        context.Request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (body?.Messages is null || body.Messages.Count == 0)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var messages = body.Messages.Select(m => new MessageParam
    {
        Role = m.Role == "user" ? Role.User : Role.Assistant,
        Content = m.Content
    }).ToList();

    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    var parameters = new MessageCreateParams
    {
        Model = Model.ClaudeOpus4_6,
        MaxTokens = 4096,
        System = "당신은 친절하고 도움이 되는 AI 어시스턴트입니다. 한국어와 영어 모두 자연스럽게 대화할 수 있습니다.",
        Messages = messages
    };

    await foreach (var streamEvent in client.Messages.CreateStreaming(parameters))
    {
        if (streamEvent.TryPickContentBlockDelta(out var delta) &&
            delta.Delta.TryPickText(out var text))
        {
            var json = JsonSerializer.Serialize(new { text = text.Text });
            var data = $"data: {json}\n\n";
            await context.Response.WriteAsync(data, Encoding.UTF8);
            await context.Response.Body.FlushAsync();
        }
    }

    await context.Response.WriteAsync("data: [DONE]\n\n", Encoding.UTF8);
    await context.Response.Body.FlushAsync();
});

app.Run();

record ChatMessage(string Role, string Content);
record ChatRequest(List<ChatMessage> Messages);
