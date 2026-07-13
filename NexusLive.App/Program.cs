using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusLive.Core;
using NexusLive.Core.Audio;
using NexusLive.Core.Inference;
using NexusLive.Core.Memory;
using NexusLive.Core.Processors;
using NexusLive.Core.State;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = "wwwroot"
});

// Add Blazor Server Services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Load & Bind configuration from appsettings.json
var llmOptions = new LlmOptions();
builder.Configuration.GetSection("Llm").Bind(llmOptions);
builder.Services.AddSingleton(llmOptions);

// Register Core Singletons
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ILlmInferenceService, LlmInferenceService>();
builder.Services.AddSingleton<IssueStateManager>();
builder.Services.AddSingleton<SlidingWindowMemory>(sp => new SlidingWindowMemory(TimeSpan.FromMinutes(10)));
builder.Services.AddSingleton<IVectorDbService, ChromaDbMockService>();
builder.Services.AddSingleton<ITranscriptionEngine, WhisperTranscriptionEngine>();

builder.Services.AddSingleton(sp => new PostSessionProcessor(
    sp.GetRequiredService<ILlmInferenceService>(),
    sp.GetRequiredService<IssueStateManager>(),
    llmOptions.AnalyticalModelName
));

builder.Services.AddSingleton(sp => new MeetingHistoryService(
    Path.Combine(Directory.GetCurrentDirectory(), "meetings")
));
builder.Services.AddSingleton<MeetingSessionCoordinator>();

var app = builder.Build();

// Disable browser caching for host document pages to ensure design updates apply
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    await next();
});

// Rebuild to apply high contrast styles and correct model names
app.UseStaticFiles();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapRazorComponents<NexusLive.App.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
