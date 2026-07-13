using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NexusLive.Core.Inference;
using Xunit;

namespace NexusLive.Tests
{
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _sendAsyncFunc;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsyncFunc)
        {
            _sendAsyncFunc = sendAsyncFunc ?? throw new ArgumentNullException(nameof(sendAsyncFunc));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _sendAsyncFunc(request);
        }
    }

    public class LlmInferenceServiceTests
    {
        [Fact]
        public async Task GenerateLiveSuggestionAsync_ValidResponse_ShouldReturnContent()
        {
            // Arrange
            var responseContent = @"{
                ""choices"": [
                    {
                        ""message"": {
                            ""role"": ""assistant"",
                            ""content"": ""Verify ChromaDB local server connectivity.""
                        }
                    }
                ]
            }";

            var handler = new MockHttpMessageHandler(req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("http://localhost:1234/v1/chat/completions", req.RequestUri?.ToString());
                
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "application/json")
                });
            });

            using var httpClient = new HttpClient(handler);
            var service = new LlmInferenceService(httpClient);

            // Act
            var result = await service.GenerateLiveSuggestionAsync("System Instruction", "User Prompt", CancellationToken.None);

            // Assert
            Assert.Equal("Verify ChromaDB local server connectivity.", result);
        }

        [Fact]
        public async Task GenerateLiveSuggestionAsync_NoSuggestionLabel_ShouldReturnEmptyString()
        {
            // Arrange
            var responseContent = @"{
                ""choices"": [
                    {
                        ""message"": {
                            ""role"": ""assistant"",
                            ""content"": ""[NO_SUGGESTION]""
                        }
                    }
                ]
            }";

            var handler = new MockHttpMessageHandler(req =>
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "application/json")
                });
            });

            using var httpClient = new HttpClient(handler);
            var service = new LlmInferenceService(httpClient);

            // Act
            var result = await service.GenerateLiveSuggestionAsync("System Instruction", "User Prompt", CancellationToken.None);

            // Assert
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public async Task GenerateLiveSuggestionAsync_HttpError_ShouldHandleGracefullyAndReturnEmpty()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(req =>
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            });

            using var httpClient = new HttpClient(handler);
            var service = new LlmInferenceService(httpClient);

            // Act
            var result = await service.GenerateLiveSuggestionAsync("System Instruction", "User Prompt", CancellationToken.None);

            // Assert
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public async Task SwitchModelAsync_ValidRequest_ShouldPostToModelControlEndpoint()
        {
            // Arrange
            var options = new LlmOptions
            {
                BaseAddress = "http://localhost:9999",
                ModelControlEndpoint = "/v1/models/load"
            };

            bool endpointCalled = false;

            var handler = new MockHttpMessageHandler(req =>
            {
                if (req.Method == HttpMethod.Post && req.RequestUri?.ToString() == "http://localhost:9999/v1/models/load")
                {
                    endpointCalled = true;
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

            using var httpClient = new HttpClient(handler);
            var service = new LlmInferenceService(httpClient, options);

            // Act
            await service.SwitchModelAsync("qwen3-7b", CancellationToken.None);

            // Assert
            Assert.True(endpointCalled);
        }

        [Fact]
        public async Task LlmInferenceService_WithOptions_ShouldUseConfiguredValues()
        {
            // Arrange
            var options = new LlmOptions
            {
                BaseAddress = "http://localhost:8888",
                LiveModelName = "custom-live-model"
            };

            bool correctModelCalled = false;

            var handler = new MockHttpMessageHandler(async req =>
            {
                var content = await req.Content!.ReadAsStringAsync();
                if (content.Contains("custom-live-model"))
                {
                    correctModelCalled = true;
                }

                var response = @"{""choices"": [{""message"": {""role"": ""assistant"", ""content"": ""OK""}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var service = new LlmInferenceService(httpClient, options);

            // Act
            await service.GenerateLiveSuggestionAsync("sys", "user", CancellationToken.None);

            // Assert
            Assert.True(correctModelCalled);
        }
    }
}
