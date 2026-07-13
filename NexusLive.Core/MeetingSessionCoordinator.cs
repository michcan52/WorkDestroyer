using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexusLive.Core.Audio;
using NexusLive.Core.Inference;
using NexusLive.Core.Memory;
using NexusLive.Core.Processors;
using NexusLive.Core.State;

namespace NexusLive.Core
{
    public class MeetingSessionCoordinator
    {
        private readonly SlidingWindowMemory _memory;
        private readonly IssueStateManager _stateManager;
        private readonly ILlmInferenceService _llmService;
        private readonly IVectorDbService _vectorDb;
        private readonly PostSessionProcessor _postProcessor;
        private readonly LlmOptions _llmOptions;
        private readonly MeetingHistoryService _historyService;

        public event Action? OnStateChanged;
        public event Action<string>? OnNewLiveSuggestion;

        public bool IsSessionActive { get; private set; }
        public string LiveSuggestion { get; private set; } = string.Empty;
        public string MeetingSummary { get; private set; } = string.Empty;
        public string ModelStatusMessage { get; private set; } = "Idle";

        public MeetingSessionCoordinator(
            SlidingWindowMemory memory,
            IssueStateManager stateManager,
            ILlmInferenceService llmService,
            IVectorDbService vectorDb,
            PostSessionProcessor postProcessor,
            LlmOptions llmOptions,
            MeetingHistoryService historyService)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
            _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
            _vectorDb = vectorDb ?? throw new ArgumentNullException(nameof(vectorDb));
            _postProcessor = postProcessor ?? throw new ArgumentNullException(nameof(postProcessor));
            _llmOptions = llmOptions ?? throw new ArgumentNullException(nameof(llmOptions));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        }

        public async Task StartSessionAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[DIAGNOSTIC] StartSessionAsync triggered. Requesting live model: '{_llmOptions.LiveModelName}'");
            IsSessionActive = true;
            LiveSuggestion = string.Empty;
            MeetingSummary = string.Empty;
            _memory.Clear();
            
            ModelStatusMessage = $"Switching to Live Model ({_llmOptions.LiveModelName})...";
            NotifyStateChanged();

            // Trigger dynamic model loading command for Live model (Phi-4-mini)
            await _llmService.SwitchModelAsync(_llmOptions.LiveModelName, cancellationToken);
            
            ModelStatusMessage = $"Live Model Loaded ({_llmOptions.LiveModelName})";
            Console.WriteLine("[DIAGNOSTIC] Live model loaded successfully. Firing state change.");
            NotifyStateChanged();
        }

        public async Task AddTranscriptSegmentAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!IsSessionActive)
            {
                Console.WriteLine($"[DIAGNOSTIC] Warning: AddTranscriptSegmentAsync called but session is inactive. Text: '{text}'");
                return;
            }

            Console.WriteLine($"[DIAGNOSTIC] AddTranscriptSegmentAsync received text: '{text}'");
            var segment = new TranscriptionSegment
            {
                Text = text,
                Timestamp = DateTime.UtcNow
            };
            _memory.AddSegment(segment);
            Console.WriteLine($"[DIAGNOSTIC] Segment added to SlidingWindowMemory. Active segment count: {_memory.GetActiveSegments().Count}");
            NotifyStateChanged();

            // Fetch context from vector db (RAG)
            Console.WriteLine("[DIAGNOSTIC] Querying local Vector DB for context RAG...");
            var ragContextList = await _vectorDb.QuerySimilarityAsync(text, 1, cancellationToken);
            string ragContext = ragContextList.Count > 0 ? ragContextList[0].Text : "";
            Console.WriteLine($"[DIAGNOSTIC] RAG Context found: '{ragContext}'");

            // Formulate prompts
            string systemPrompt = $@"You are the Real-Time Assistant module of NexusLive. Base your suggestions only on the context of the last 10 minutes.
Do not suggest actions for resolved issues. Be extremely concise.
ACTIVE ISSUES: {_stateManager.ToJsonString()}
PREVIOUS MEETINGS CONTEXT: {ragContext}";

            string userPrompt = $"Last 10 minutes: {_memory.GetFormattedContext()}\n\nWhat are the suggestions?";

            ModelStatusMessage = "Analyzing and generating real-time suggestions...";
            NotifyStateChanged();

            Console.WriteLine("[DIAGNOSTIC] Sending request to local Live suggestion LLM...");
            string suggestion = await _llmService.GenerateLiveSuggestionAsync(systemPrompt, userPrompt, cancellationToken);
            LiveSuggestion = string.IsNullOrWhiteSpace(suggestion) ? string.Empty : suggestion;
            Console.WriteLine($"[DIAGNOSTIC] Live suggestion result: '{(string.IsNullOrEmpty(LiveSuggestion) ? "[NO_SUGGESTION] or server offline" : LiveSuggestion)}'");
            
            ModelStatusMessage = $"Live Model Active ({_llmOptions.LiveModelName})";
            OnNewLiveSuggestion?.Invoke(LiveSuggestion);
            NotifyStateChanged();
        }

        public async Task EndSessionAsync(string stateFilePath, CancellationToken cancellationToken = default)
        {
            if (!IsSessionActive) return;

            Console.WriteLine("[DIAGNOSTIC] EndSessionAsync triggered. Switching to Analytical model...");
            ModelStatusMessage = $"Switching to Analytical Model ({_llmOptions.AnalyticalModelName})...";
            NotifyStateChanged();

            string fullTranscript = _memory.GetFormattedContext();
            
            // PostSessionProcessor handles switching model internally and generating summary/updating issues
            Console.WriteLine($"[DIAGNOSTIC] Invoking PostSessionProcessor with model: {_llmOptions.AnalyticalModelName}...");
            MeetingSummary = await _postProcessor.ProcessSessionEndAsync(fullTranscript, stateFilePath, cancellationToken);
            Console.WriteLine($"[DIAGNOSTIC] Post-session summary generated. Length: {MeetingSummary?.Length ?? 0}");

            // Save the meeting history to local persistent JSON file
            if (!string.IsNullOrWhiteSpace(MeetingSummary) && !string.IsNullOrWhiteSpace(fullTranscript))
            {
                await _historyService.SaveMeetingAsync(MeetingSummary, fullTranscript).ConfigureAwait(false);
            }

            // Eject/unload the analytical model after processing to clean up GPU/RAM
            await _llmService.UnloadModelAsync(_llmOptions.AnalyticalModelName, cancellationToken).ConfigureAwait(false);
            
            IsSessionActive = false;
            ModelStatusMessage = "Session Concluded (Model Ejected)";
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnStateChanged?.Invoke();
    }
}
