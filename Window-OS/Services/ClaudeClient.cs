using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace ManagementHouseFee.Services
{
    /// <summary>
    /// Claude Messages API를 REST로 직접 호출하는 클라이언트.
    /// SDK 없이 HttpClient만 사용한다.
    /// </summary>
    public class ClaudeClient
    {
        public const string Model = "claude-haiku-5-5";

        private readonly HttpClient _http;

        public ClaudeClient(string apiKey)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri("https://api.anthropic.com/"),
                Timeout = TimeSpan.FromSeconds(60)
            };
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }

        /// <summary>환경변수 ANTHROPIC_API_KEY에서 키를 읽어 만든다. 키를 코드에 넣지 않기 위함.</summary>
        public static ClaudeClient FromEnvironment()
        {
            var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("ANTHROPIC_API_KEY 환경변수가 없습니다.");
            return new ClaudeClient(key);
        }

        /// <param name="toolChoice">특정 도구 호출을 강제할 때 사용. 예: {"type":"tool","name":"record_fee_bill"}</param>
        public async Task<JsonNode> SendAsync(string system, JsonArray tools, JsonArray messages,
                                              JsonObject? toolChoice = null, int maxTokens = 1024)
        {
            var body = new JsonObject
            {
                ["model"] = Model,
                ["max_tokens"] = maxTokens,
                ["system"] = system,
                ["messages"] = messages.DeepClone()
            };
            if (tools.Count > 0) body["tools"] = tools.DeepClone(); // 도구 없는 비교 실험용
            if (toolChoice != null) body["tool_choice"] = toolChoice.DeepClone();

            using var content = new StringContent(body.ToJsonString(FeeAgent.JsonOpt), Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync("v1/messages", content);
            string text = await res.Content.ReadAsStringAsync();

            // 실패 시 응답 본문까지 보여 줘야 원인을 알 수 있다.
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"Claude API 오류 {(int)res.StatusCode}: {text}");

            return JsonNode.Parse(text)!;
        }
    }
}
