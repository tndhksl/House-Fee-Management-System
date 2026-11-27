using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ManagementHouseFee.Models;

namespace ManagementHouseFee.Services
{
    /// <summary>고지서에서 읽은 항목 1개. 이름은 고지서 표기 그대로 둔다.</summary>
    public record ExtractedItem(string Name, long Amount);

    /// <summary>고지서 캡처 추출 결과와 자동 점검 결과.</summary>
    public class BillImportResult
    {
        public int? Year { get; set; }
        public int? Month { get; set; }

        /// <summary>고지서에 적힌 총액. 화면에 없으면 null.</summary>
        public long? StatedTotal { get; set; }

        /// <summary>중복 제거까지 끝난 항목들</summary>
        public List<ExtractedItem> Items { get; } = new();

        /// <summary>자동 처리 내역 (중복 제거, 0원 제외 등). 사용자에게 그대로 보여 준다.</summary>
        public List<string> Notes { get; } = new();

        /// <summary>같은 이름인데 금액이 다르게 읽힌 항목. 사용자가 반드시 확인해야 한다.</summary>
        public List<string> Conflicts { get; } = new();

        public long ItemSum => Items.Sum(i => i.Amount);

        /// <summary>합계 점검: 항목 합계와 고지서 총액이 같은가. 총액을 못 읽었으면 null.</summary>
        public bool? TotalMatches => StatedTotal is null ? null : StatedTotal == ItemSum;

        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    /// <summary>
    /// 관리비 고지서 캡처 이미지를 읽어 항목별 금액을 뽑는다.
    /// AI는 '읽기'만 하고, 중복 제거·합계 점검·항목 분류·저장은 코드와 사용자가 한다.
    /// </summary>
    public class BillImporter
    {
        private const long MaxImageBytes = 5 * 1024 * 1024; // API 이미지 크기 제한

        private const string SystemPrompt =
            "너는 한국 아파트 관리비 고지서 캡처를 읽어 record_fee_bill 도구로 기록하는 역할만 한다.\n" +
            "규칙:\n" +
            "1. 여러 장의 캡처는 같은 달 고지서를 나눠 찍은 것이다. 화면이 겹쳐 같은 항목이 두 번 보여도 한 번만 기록한다.\n" +
            "2. 상세내역의 상위 항목만 기록한다. 한 항목 아래 작은 글씨로 들여쓴 세부 내역은 그 상위 항목에 이미 포함된 금액이므로 기록하지 않는다.\n" +
            "3. 금액 아래나 옆에 ▲, ▼ 기호와 함께 붙은 숫자는 전월 대비 증감액이다. 금액이 아니므로 절대 기록하지 않는다.\n" +
            "4. kWh, ㎥ 같은 사용량은 금액이 아니다.\n" +
            "5. 할인내역 영역과 광고 배너의 금액은 기록하지 않는다. 단, 상세내역 안의 '관리비차감'처럼 음수로 표시된 항목은 음수 그대로 기록한다.\n" +
            "6. 총액은 'N월분 관리비' 바로 아래의 큰 금액이다. '지난달보다'와 함께 있는 숫자는 증감액이므로 총액이 아니다. 화면에 없으면 null로 둔다.\n" +
            "7. 항목 이름은 고지서 표기를 그대로 쓴다. 바꾸거나 합치지 않는다.\n" +
            "8. '26년 10월분'처럼 두 자리 연도는 2026처럼 네 자리로 쓴다.";

        private readonly ClaudeClient _client;

        public BillImporter(ClaudeClient client)
        {
            _client = client;
        }

        public async Task<BillImportResult> ExtractAsync(IReadOnlyList<string> imagePaths)
        {
            if (imagePaths.Count == 0) throw new ArgumentException("이미지를 한 장 이상 선택하세요.");

            var content = new JsonArray();
            for (int i = 0; i < imagePaths.Count; i++)
            {
                var path = imagePaths[i];
                var info = new FileInfo(path);
                if (info.Length > MaxImageBytes)
                    throw new ArgumentException($"{info.Name}이(가) 5MB를 넘습니다.");

                content.Add(new JsonObject { ["type"] = "text", ["text"] = $"캡처 {i + 1}/{imagePaths.Count}" });
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = MediaType(path),
                        ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(path))
                    }
                });
            }
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "위 캡처들은 같은 달 관리비 고지서다. 규칙에 따라 record_fee_bill로 기록해." });

            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } };

            // record_fee_bill 도구 호출을 강제 → 자유 문장이 아니라 정해진 JSON 형식으로만 받는다.
            var resp = await _client.SendAsync(SystemPrompt, new JsonArray { BuildTool() }, messages,
                toolChoice: new JsonObject { ["type"] = "tool", ["name"] = "record_fee_bill" },
                maxTokens: 2048);

            var toolUse = resp["content"]!.AsArray().FirstOrDefault(b => (string?)b?["type"] == "tool_use")
                          ?? throw new InvalidOperationException("AI가 고지서 기록 도구를 호출하지 않았습니다.");

            var result = Postprocess(toolUse["input"]!);
            result.InputTokens = (int?)resp["usage"]?["input_tokens"] ?? 0;
            result.OutputTokens = (int?)resp["usage"]?["output_tokens"] ?? 0;
            return result;
        }

        /// <summary>
        /// AI가 돌려준 값을 코드로 정리한다. (API 없이도 테스트할 수 있게 분리)
        ///  - 같은 이름·같은 금액 → 중복으로 보고 하나만 남김
        ///  - 같은 이름·다른 금액 → 충돌로 표시, 사용자 확인 필요
        ///  - 0원 항목 → 제외
        /// </summary>
        public static BillImportResult Postprocess(JsonNode input)
        {
            var result = new BillImportResult();

            int? year = (int?)input["billing_year"];
            if (year is >= 0 and < 100) year += 2000;
            result.Year = year;
            result.Month = (int?)input["billing_month"];
            result.StatedTotal = (long?)input["total_amount"];

            if (result.Month is < 1 or > 12)
            {
                result.Conflicts.Add($"월을 잘못 읽었습니다: {result.Month}");
                result.Month = null;
            }

            var seen = new Dictionary<string, long>();
            foreach (var node in input["items"]?.AsArray() ?? new JsonArray())
            {
                string name = ((string?)node?["name"] ?? "").Trim();
                long? amount = (long?)node?["amount"];
                if (name.Length == 0 || amount is null) continue;

                if (amount == 0)
                {
                    result.Notes.Add($"0원 항목 제외: {name}");
                    continue;
                }

                if (seen.TryGetValue(name, out var prev))
                {
                    if (prev == amount) result.Notes.Add($"중복 제거: {name} {amount:N0}원 (여러 캡처에 반복)");
                    else result.Conflicts.Add($"{name}: {prev:N0}원과 {amount:N0}원이 함께 읽혔습니다. 고지서를 확인하세요.");
                    continue;
                }

                seen[name] = amount.Value;
                result.Items.Add(new ExtractedItem(name, amount.Value));
            }

            return result;
        }

        private static JsonObject BuildTool() => new()
        {
            ["name"] = "record_fee_bill",
            ["description"] = "관리비 고지서 캡처에서 읽은 청구 연·월, 총액, 상세내역의 상위 항목별 금액을 기록한다. 증감액(▲▼), 사용량, 세부 내역, 할인내역, 광고 금액은 기록하지 않는다.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["billing_year"] = new JsonObject { ["type"] = "integer", ["description"] = "청구 연도 네 자리, 예: 2026" },
                    ["billing_month"] = new JsonObject { ["type"] = "integer", ["description"] = "청구 월, 1~12. 'N월분'의 N" },
                    ["total_amount"] = new JsonObject
                    {
                        ["type"] = new JsonArray("integer", "null"),
                        ["description"] = "'N월분 관리비' 아래의 총액(원). 화면에 없으면 null. 전월 대비 증감액이 아님"
                    },
                    ["items"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "상세내역의 상위 항목들. 고지서에 나온 순서대로",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "고지서 표기 그대로의 항목명" },
                                ["amount"] = new JsonObject { ["type"] = "integer", ["description"] = "청구 금액(원). 차감 항목은 음수" }
                            },
                            ["required"] = new JsonArray("name", "amount")
                        }
                    }
                },
                ["required"] = new JsonArray("billing_year", "billing_month", "total_amount", "items")
            }
        };

        private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => throw new ArgumentException($"지원하지 않는 이미지 형식입니다: {Path.GetFileName(path)}")
        };
    }

    /// <summary>
    /// 고지서 항목명 → 앱 항목(전기세, 수도세 ...) 매핑.
    /// 분류는 AI가 아니라 이 표가 결정한다. 처음 보는 이름은 사용자가 고르고, 고른 결과를 표에 저장해 다음부터 자동 분류한다.
    /// </summary>
    public class CategoryMapper
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _map;

        public static readonly Dictionary<string, string> Defaults = new()
        {
            ["세대전기료"] = "전기세",
            ["공동전기료"] = "전기세",
            ["전력기금"] = "전기세",
            ["공동전력기금"] = "전기세",
            ["세대수도료"] = "수도세",
            ["공동수도료"] = "수도세",
            ["세대가스료"] = "가스세",
            ["가스료"] = "가스세",
            ["일반관리비"] = "관리비",
            ["소독비"] = "관리비",
            ["승강기유지비"] = "관리비",
            ["수선유지비"] = "관리비",
            ["장기수선충당금"] = "관리비",
            ["정화조청소비"] = "관리비",
            ["건물보험료"] = "관리비",
            ["도급용역비"] = "관리비",
            ["소방안전대행료"] = "관리비",
            ["전기안전대행료"] = "관리비",
            ["위탁관리수수료"] = "관리비",
            ["공동청소비"] = "관리비",
            ["관리비차감"] = "관리비"
        };

        public CategoryMapper(string? path = null)
        {
            _path = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "category_map.json");
            _map = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new(Defaults)
                : new Dictionary<string, string>(Defaults);
        }

        /// <summary>매핑된 앱 항목. 처음 보는 이름이면 null → 사용자가 골라야 함</summary>
        public string? Map(string billName) => _map.TryGetValue(billName, out var c) ? c : null;

        /// <summary>사용자가 고른 분류를 기억해 저장한다.</summary>
        public void Remember(string billName, string category)
        {
            _map[billName] = category;
            File.WriteAllText(_path, JsonSerializer.Serialize(_map, FeeAgent.JsonOpt));
        }

        /// <summary>분류가 끝난 항목들을 앱 항목별로 더해 FeeItem 목록으로 만든다.</summary>
        public static List<FeeItem> Aggregate(IEnumerable<(string Category, long Amount)> rows) =>
            rows.GroupBy(r => r.Category)
                .Select(g => new FeeItem { Name = g.Key, Amount = g.Sum(r => r.Amount) })
                .ToList();
    }
}