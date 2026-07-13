namespace NexusLive.Core.Inference
{
    public class LlmOptions
    {
        public string BaseAddress { get; set; } = "http://localhost:1234";
        public string LiveModelName { get; set; } = "phi-4-mini";
        public string AnalyticalModelName { get; set; } = "qwen3-7b";
        public string ModelControlEndpoint { get; set; } = "/v1/models";
    }
}
