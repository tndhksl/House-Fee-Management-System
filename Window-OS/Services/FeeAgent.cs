using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ManagementHouseFee.Models;

namespace ManagementHouseFee.Services
{
    /// <summary>도구 호출 1건의 기록. 화면에 그대로 보여 준다.</summary>
    public record ToolCallLog(string Name, string Input, string Output, bool Allowed);

    /// <summary>질문 1건에 대한 에이전트 실행 결과.</summary>
    public class AgentResult
    {
        public string Answer { get; set; } = "";
        public List<ToolCallLog> ToolCalls { get; } = new();

        /// <summary>답변에 나왔지만 도구 결과·질문 어디에도 없는 숫자. 비어 있어야 검증 통과.</summary>
        public List<string> UnverifiedNumbers { get; } = new();
        public bool Verified => UnverifiedNumbers.Count == 0;

        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int Rounds { get; set; }
    }

    /// <summary>
    /// 관리비 데이터를 도구로 열어 두고, Claude가 그 도구를 호출해 질문에 답하게 하는 에이전트.
    /// 원칙
    ///  1. 허용한 도구 3개만 실행한다. (화이트리스트)
    ///  2. 계산은 FeeAnalysisService가 한다. LLM은 결과를 설명만 한다.
    ///  3. 답변 속 숫자가 실제 도구 결과에 있는지 검증한다.
    ///  4. 원본 JSON 파일은 보내지 않고, 도구가 계산한 결과만 보낸다.
    /// </summary>
    public class FeeAgent
    {
        // 한글을 \uXXXX로 바꾸지 않고 그대로 직렬화 (로그 가독성, 토큰 절약)
        internal static readonly JsonSerializerOptions JsonOpt = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

        private const int MaxRounds = 5; // 도구 호출 반복 상한 (무한 루프 방지)

        private const string SystemPrompt =
            "너는 가정용 관리비 분석 도우미다.\n" +
            "- 금액, 증감, 비율은 반드시 도구로 확인한 값만 말한다. 도구 결과에 없는 숫자를 지어내지 않는다.\n" +
            "- 합계나 차이를 직접 계산하지 말고 도구 결과에 있는 값을 그대로 쓴다.\n" +
            "- 도구가 데이터 없음을 돌려주면 데이터가 없다고 답한다.\n" +
            "- 금액은 12,345원처럼 원 단위 숫자로 쓴다.\n" +
            "- 연도를 말하지 않으면 데이터에 있는 가장 최근 달을 기준으로 한다. 가장 최근 달은 get_latest_month로 확인한다.\n" +
            "- 한국어로 3문장 이내로 답한다.";

        private readonly ClaudeClient _client;
        private readonly FeeAnalysisService _analysis;
        private readonly List<FeeRecord> _records;
        private readonly JsonArray _tools = BuildTools();

        public FeeAgent(ClaudeClient client, List<FeeRecord> records)
        {
            _client = client;
            _records = records;
            _analysis = new FeeAnalysisService(records);
        }

        public async Task<AgentResult> AskAsync(string question)
        {
            var result = new AgentResult();
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = question }
            };

            for (int round = 1; round <= MaxRounds; round++)
            {
                result.Rounds = round;
                var resp = await _client.SendAsync(SystemPrompt, _tools, messages);

                result.InputTokens += (int?)resp["usage"]?["input_tokens"] ?? 0;
                result.OutputTokens += (int?)resp["usage"]?["output_tokens"] ?? 0;

                var content = resp["content"]!.AsArray();
                // 받은 응답은 그대로 대화 기록에 붙인다. (tool_use 블록을 지우면 다음 요청이 실패한다)
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

                if ((string?)resp["stop_reason"] != "tool_use")
                {
                    result.Answer = string.Concat(content
                        .Where(b => (string?)b?["type"] == "text")
                        .Select(b => (string?)b!["text"])).Trim();
                    Verify(question, result);
                    return result;
                }

                // 도구 실행 → tool_result로 돌려주기
                var toolResults = new JsonArray();
                foreach (var block in content)
                {
                    if ((string?)block?["type"] != "tool_use") continue;

                    string name = (string)block["name"]!;
                    JsonNode input = block["input"] ?? new JsonObject();
                    var (output, allowed, isError) = RunTool(name, input);

                    result.ToolCalls.Add(new ToolCallLog(name, input.ToJsonString(JsonOpt), output, allowed));

                    var tr = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = (string)block["id"]!,
                        ["content"] = output
                    };
                    if (isError) tr["is_error"] = true;
                    toolResults.Add(tr);
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }

            result.Answer = "도구 호출이 너무 많이 반복되어 중단했습니다.";
            return result;
        }

        #region [비교 실험: 도구 없이 원본 데이터를 통째로 주는 방식]

        private const string BaselinePrompt =
            "너는 가정용 관리비 분석 도우미다. 아래 JSON이 전체 관리비 기록이다. " +
            "필요한 계산은 직접 해서 답한다. 금액은 12,345원처럼 원 단위 숫자로 쓴다. " +
            "가장 최근 달을 '이번 달'로 본다. 한국어로 3문장 이내로 답한다.\n\n데이터:\n";

        /// <summary>
        /// 비교용. 도구 없이 원본 데이터를 프롬프트에 넣고 LLM이 직접 계산하게 한다.
        /// 검증은 원본 데이터에 있는 숫자만 '근거 있음'으로 본다.
        /// </summary>
        public async Task<AgentResult> AskWithoutToolsAsync(string question)
        {
            var result = new AgentResult { Rounds = 1 };

            string data = JsonSerializer.Serialize(
                _records.OrderBy(r => r.Year).ThenBy(r => r.Month).Select(r => new
                {
                    year = r.Year,
                    month = r.Month,
                    items = r.Items.Select(i => new { name = i.Name, amount = Won(i.Amount) })
                }), JsonOpt);

            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = question } };
            var resp = await _client.SendAsync(BaselinePrompt + data, new JsonArray(), messages);

            result.InputTokens = (int?)resp["usage"]?["input_tokens"] ?? 0;
            result.OutputTokens = (int?)resp["usage"]?["output_tokens"] ?? 0;
            result.Answer = string.Concat(resp["content"]!.AsArray()
                .Where(b => (string?)b?["type"] == "text")
                .Select(b => (string?)b!["text"])).Trim();

            // 원본 데이터를 '근거'로 등록하고 같은 방식으로 검증
            result.ToolCalls.Add(new ToolCallLog("raw_data(비교용)", "", data, true));
            Verify(question, result);
            return result;
        }

        #endregion

        #region [도구 정의와 실행]

        private static JsonArray BuildTools()
        {
            static JsonObject Int(string desc) => new() { ["type"] = "integer", ["description"] = desc };

            return new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "get_latest_month",
                    ["description"] = "관리비 기록이 있는 가장 최근 연·월을 돌려준다. 사용자가 '이번 달', '지난달'처럼 말하면 먼저 호출해 기준 달을 정한다.",
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_month_detail",
                    ["description"] = "특정 연·월의 관리비 총액과 항목별 금액을 돌려준다.",
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["year"] = Int("연도, 예: 2026"), ["month"] = Int("월, 1~12") },
                        ["required"] = new JsonArray("year", "month")
                    }
                },
                new JsonObject
                {
                    ["name"] = "compare_months",
                    ["description"] = "두 달의 관리비를 항목별로 비교해 증감액, 증감률, 상태를 돌려준다. 증가·감소 이유를 물으면 반드시 이 도구로 확인한다.",
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["year"] = Int("비교할 달의 연도"),
                            ["month"] = Int("비교할 달, 1~12"),
                            ["base_year"] = Int("기준 달의 연도"),
                            ["base_month"] = Int("기준 달, 1~12")
                        },
                        ["required"] = new JsonArray("year", "month", "base_year", "base_month")
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_item_trend",
                    ["description"] = "한 항목의 최근 N개월 금액 추이를 돌려준다. 기록이 없는 달은 amount가 null이다.",
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["item"] = new JsonObject { ["type"] = "string", ["description"] = "항목 이름, 예: 전기세" },
                            ["end_year"] = Int("마지막 달의 연도"),
                            ["end_month"] = Int("마지막 달, 1~12"),
                            ["months"] = Int("조회할 개월 수, 1~24")
                        },
                        ["required"] = new JsonArray("item", "end_year", "end_month", "months")
                    }
                }
            };
        }

        /// <summary>화이트리스트에 있는 도구만 실행한다. 입력값도 범위를 검사한다.</summary>
        private (string Output, bool Allowed, bool IsError) RunTool(string name, JsonNode input)
        {
            try
            {
                switch (name)
                {
                    case "get_latest_month":
                        return (LatestMonth(), true, false);

                    case "get_month_detail":
                    {
                        int y = ReadInt(input, "year", 2000, 2100), m = ReadInt(input, "month", 1, 12);
                        return (MonthDetail(y, m), true, false);
                    }

                    case "compare_months":
                    {
                        int y = ReadInt(input, "year", 2000, 2100), m = ReadInt(input, "month", 1, 12);
                        int by = ReadInt(input, "base_year", 2000, 2100), bm = ReadInt(input, "base_month", 1, 12);
                        return (CompareMonths(y, m, by, bm), true, false);
                    }

                    case "get_item_trend":
                    {
                        string item = (string?)input["item"] ?? throw new ArgumentException("item이 없습니다.");
                        int y = ReadInt(input, "end_year", 2000, 2100), m = ReadInt(input, "end_month", 1, 12);
                        int n = ReadInt(input, "months", 1, 24);
                        return (ItemTrend(item, y, m, n), true, false);
                    }

                    default:
                        // 등록하지 않은 도구는 실행하지 않는다.
                        return (Error($"허용되지 않은 도구입니다: {name}"), false, true);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                return (Error(ex.Message), true, true);
            }
        }

        private static int ReadInt(JsonNode input, string key, int min, int max)
        {
            var node = input[key] ?? throw new ArgumentException($"{key}가 없습니다.");
            int v = node.GetValue<int>();
            if (v < min || v > max) throw new ArgumentException($"{key}={v}는 허용 범위 {min}~{max}를 벗어났습니다.");
            return v;
        }

        private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString(JsonOpt);

        private static long Won(double amount) => (long)Math.Round(amount);

        private string LatestMonth()
        {
            var latest = _records.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).FirstOrDefault();
            if (latest == null) return new JsonObject { ["found"] = false }.ToJsonString(JsonOpt);
            return new JsonObject { ["found"] = true, ["year"] = latest.Year, ["month"] = latest.Month }.ToJsonString(JsonOpt);
        }

        private string MonthDetail(int year, int month)
        {
            var r = _analysis.GetRecord(year, month);
            if (r == null)
                return new JsonObject { ["year"] = year, ["month"] = month, ["found"] = false }.ToJsonString(JsonOpt);

            var items = new JsonArray();
            foreach (var i in r.Items) items.Add(new JsonObject { ["name"] = i.Name, ["amount"] = Won(i.Amount) });

            return new JsonObject
            {
                ["year"] = year, ["month"] = month, ["found"] = true,
                ["total"] = Won(r.TotalAmount), ["items"] = items
            }.ToJsonString(JsonOpt);
        }

        private string CompareMonths(int year, int month, int baseYear, int baseMonth)
        {
            var changes = _analysis.Compare(year, month, baseYear, baseMonth);
            if (changes == null)
                return new JsonObject { ["found"] = false, ["reason"] = $"{year}년 {month}월 기록이 없습니다." }.ToJsonString(JsonOpt);

            var baseRecord = _analysis.GetRecord(baseYear, baseMonth);
            var current = _analysis.GetRecord(year, month)!;

            var items = new JsonArray();
            foreach (var c in changes)
            {
                items.Add(new JsonObject
                {
                    ["name"] = c.Name,
                    ["current"] = Won(c.Current),
                    ["previous"] = c.Previous is null ? null : Won(c.Previous.Value),
                    ["diff"] = c.Diff is null ? null : Won(c.Diff.Value),
                    ["rate_percent"] = c.Rate is null ? null : Math.Round(c.Rate.Value * 100, 1),
                    ["status"] = c.Status.ToString()
                });
            }

            // 기준 달에만 있고 이번 달에는 없는 항목도 알려 준다.
            var removed = new JsonArray();
            if (baseRecord != null)
                foreach (var b in baseRecord.Items.Where(b => current.Items.All(i => i.Name != b.Name)))
                    removed.Add(new JsonObject { ["name"] = b.Name, ["previous"] = Won(b.Amount) });

            return new JsonObject
            {
                ["found"] = true,
                ["base_found"] = baseRecord != null,
                ["total_current"] = Won(current.TotalAmount),
                ["total_previous"] = baseRecord == null ? null : Won(baseRecord.TotalAmount),
                ["total_diff"] = baseRecord == null ? null : Won(current.TotalAmount - baseRecord.TotalAmount),
                ["items"] = items,
                ["removed_items"] = removed
            }.ToJsonString(JsonOpt);
        }

        private string ItemTrend(string item, int endYear, int endMonth, int months)
        {
            var points = new JsonArray();
            var d = new DateTime(endYear, endMonth, 1).AddMonths(-(months - 1));
            for (int k = 0; k < months; k++, d = d.AddMonths(1))
            {
                var amount = _analysis.GetRecord(d.Year, d.Month)?.Items.FirstOrDefault(i => i.Name == item)?.Amount;
                points.Add(new JsonObject
                {
                    ["year"] = d.Year, ["month"] = d.Month,
                    ["amount"] = amount is null ? null : Won(amount.Value)
                });
            }
            return new JsonObject { ["item"] = item, ["points"] = points }.ToJsonString(JsonOpt);
        }

        #endregion

        #region [답변 검증]

        private static readonly Regex NumberPattern = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

        /// <summary>
        /// 답변에 나온 숫자가 도구 결과·도구 입력·질문 중 어딘가에 실제로 있는지 확인한다.
        /// 어디에도 없는 숫자는 LLM이 만들어 낸 값일 수 있으므로 표시한다.
        /// </summary>
        private static void Verify(string question, AgentResult result)
        {
            var known = new HashSet<decimal>();
            foreach (var call in result.ToolCalls)
            {
                AddNumbers(call.Output, known);
                AddNumbers(call.Input, known);
            }
            AddNumbers(question, known);

            foreach (Match m in NumberPattern.Matches(result.Answer))
            {
                if (!TryParse(m.Value, out var v)) continue;
                if (v <= 12) continue; // 월(1~12)이나 '3개월' 같은 작은 수는 검증 대상에서 제외
                if (!known.Contains(v) && !known.Contains(-v))
                    result.UnverifiedNumbers.Add(m.Value);
            }
        }

        private static void AddNumbers(string text, HashSet<decimal> set)
        {
            foreach (Match m in NumberPattern.Matches(text))
                if (TryParse(m.Value, out var v)) set.Add(v);
        }

        private static bool TryParse(string raw, out decimal value) =>
            decimal.TryParse(raw.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

        #endregion
    }
}
