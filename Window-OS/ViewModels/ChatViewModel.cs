using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagementHouseFee.Services;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace ManagementHouseFee.ViewModels
{
    /// <summary>채팅 말풍선 1개</summary>
    public partial class ChatMessage : ObservableObject
    {
        public bool IsUser { get; init; }

        [ObservableProperty] private string _text = "";

        /// <summary>검증 결과 + 도구·토큰 정보 (봇 답변에만)</summary>
        [ObservableProperty] private string _detail = "";
        [ObservableProperty] private bool _isWarning;

        /// <summary>이 답변을 만들 때 호출된 도구 기록</summary>
        public ObservableCollection<ToolCallLog> ToolCalls { get; } = new();
    }

    /// <summary>
    /// 화면 오른쪽 아래 챗봇. 어느 화면에서든 열 수 있다.
    /// 질문할 때마다 최신 관리비 데이터를 다시 읽어서, 방금 저장한 내역도 바로 반영된다.
    /// </summary>
    public partial class ChatViewModel : ObservableObject
    {
        private readonly ClaudeClient? _client;

        public ObservableCollection<ChatMessage> Messages { get; } = new();

        public List<string> Examples { get; } = new()
        {
            "이번 달 관리비 왜 올랐어?",
            "작년 같은 달이랑 비교해 줘",
            "전기세 최근 6개월 추이"
        };

        [ObservableProperty] private bool _isOpen;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private string _input = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendExampleCommand))]
        private bool _isBusy;

        public ChatViewModel()
        {
            Messages.Add(new ChatMessage
            {
                Text = "안녕하세요! 관리비에 대해 물어보세요.\n앱에 저장된 기록을 직접 계산해서, 확인된 숫자로만 답해 드려요."
            });

            try
            {
                _client = ClaudeClient.FromEnvironment();
            }
            catch (InvalidOperationException ex)
            {
                Messages.Add(new ChatMessage { Text = ex.Message + " 환경변수를 설정한 뒤 프로그램을 다시 실행하세요.", IsWarning = true });
            }
        }

        [RelayCommand]
        private void Toggle() => IsOpen = !IsOpen;

        private bool CanSend() => _client != null && !IsBusy && !string.IsNullOrWhiteSpace(Input);
        private bool CanSendExample(string? _) => _client != null && !IsBusy;

        [RelayCommand(CanExecute = nameof(CanSend))]
        private async Task SendAsync()
        {
            string q = Input.Trim();
            Input = "";
            await AskAsync(q);
        }

        [RelayCommand(CanExecute = nameof(CanSendExample))]
        private async Task SendExampleAsync(string? example)
        {
            if (!string.IsNullOrWhiteSpace(example)) await AskAsync(example);
        }

        /// <summary>도구 호출 기록(JSON)을 사람이 읽기 쉬운 출처 문장으로 바꾼다.</summary>
        private static string DescribeSources(IEnumerable<ToolCallLog> calls)
        {
            var parts = new List<string>();
            foreach (var c in calls.Where(c => c.Allowed))
            {
                JsonNode? i = null;
                try { i = JsonNode.Parse(c.Input); } catch { /* 입력 파싱 실패 시 이름만 */ }

                string? text = c.Name switch
                {
                    "get_month_detail" => $"{i?["year"]}년 {i?["month"]}월 내역",
                    "compare_months" => $"{i?["year"]}년 {i?["month"]}월과 {i?["base_year"]}년 {i?["base_month"]}월 비교",
                    "get_item_trend" => $"{i?["item"]} 최근 {i?["months"]}개월 추이",
                    _ => null // get_latest_month 같은 보조 조회는 표시하지 않음
                };
                if (text != null && !parts.Contains(text)) parts.Add(text);
            }
            return string.Join(", ", parts);
        }

        private async Task AskAsync(string question)
        {
            IsBusy = true;
            Messages.Add(new ChatMessage { IsUser = true, Text = question });
            var bot = new ChatMessage { Text = "계산하는 중..." };
            Messages.Add(bot);

            try
            {
                // 질문할 때마다 최신 데이터로 에이전트를 만든다.
                var agent = new FeeAgent(_client!, new DataService().Load());
                var r = await agent.AskAsync(question);

                bot.Text = r.Answer;
                foreach (var call in r.ToolCalls) bot.ToolCalls.Add(call);
                bot.IsWarning = !r.Verified;
                string sources = DescribeSources(r.ToolCalls);
                bot.Detail = (r.Verified
                                 ? "✔ 앱 계산 결과와 일치"
                                 : $"⚠ 계산 결과에 없는 숫자: {string.Join(", ", r.UnverifiedNumbers)}")
                             + (sources.Length > 0 ? $"\n참고한 데이터: {sources}" : "");
            }
            catch (Exception ex)
            {
                bot.Text = $"오류가 발생했습니다: {ex.Message}";
                bot.IsWarning = true;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
