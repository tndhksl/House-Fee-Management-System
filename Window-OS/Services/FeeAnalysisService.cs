using ManagementHouseFee.Models;

namespace ManagementHouseFee.Services
{
    /// <summary>
    /// 비교 결과 상태.
    /// "비교할 달의 기록이 없음"과 "그 달에 이 항목이 없었음"을 구분하기 위해 따로 둔다.
    /// (기존 코드는 두 경우를 모두 0원으로 처리했음)
    /// </summary>
    public enum CompareStatus
    {
        NoBaseRecord, // 비교 대상 달의 기록 자체가 없음
        NewItem,      // 비교 대상 달에는 없던 항목
        Same,         // 금액 동일
        Increased,    // 증가
        Decreased     // 감소
    }

    /// <summary>
    /// 항목 하나의 비교 결과. 화면(ViewModel)과 AI Tool이 같이 쓰는 순수 데이터라 WPF 타입을 쓰지 않는다.
    /// </summary>
    public class ItemChange
    {
        public string Name { get; init; } = "";
        public double Current { get; init; }
        public double? Previous { get; init; }   // null이면 비교할 값이 없음
        public CompareStatus Status { get; init; }

        public double? Diff => Previous is null ? null : Current - Previous.Value;

        // 이전 값이 0이면 비율을 계산할 수 없으므로 null
        public double? Rate => (Previous is null || Previous.Value == 0)
            ? null
            : (Current - Previous.Value) / Previous.Value;
    }

    /// <summary>
    /// 관리비 비교·분석 계산을 한 곳에 모은 서비스.
    /// 화면과 AI가 같은 함수를 쓰게 해서, 화면에 보이는 숫자와 AI가 말하는 숫자가 달라지지 않게 한다.
    /// </summary>
    public class FeeAnalysisService
    {
        private readonly List<FeeRecord> _records;

        public FeeAnalysisService(List<FeeRecord> records)
        {
            _records = records;
        }

        /// <summary>해당 연·월의 기록. 없으면 null.</summary>
        public FeeRecord? GetRecord(int year, int month)
        {
            return _records.FirstOrDefault(r => r.Year == year && r.Month == month);
        }

        /// <summary>
        /// (year, month)의 항목들을 (baseYear, baseMonth)와 비교한다.
        /// 기준이 되는 이번 달 기록이 없으면 null.
        /// </summary>
        public List<ItemChange>? Compare(int year, int month, int baseYear, int baseMonth)
        {
            var current = GetRecord(year, month);
            if (current?.Items == null) return null;

            var baseRecord = GetRecord(baseYear, baseMonth);

            return current.Items.Select(item =>
            {
                FeeItem? prev = baseRecord?.Items?.FirstOrDefault(i => i.Name == item.Name);

                CompareStatus status =
                    baseRecord == null ? CompareStatus.NoBaseRecord :
                    prev == null ? CompareStatus.NewItem :
                    item.Amount == prev.Amount ? CompareStatus.Same :
                    item.Amount > prev.Amount ? CompareStatus.Increased :
                                                CompareStatus.Decreased;

                return new ItemChange
                {
                    Name = item.Name,
                    Current = item.Amount,
                    Previous = prev?.Amount,
                    Status = status
                };
            }).ToList();
        }

        /// <summary>지난달 대비</summary>
        public List<ItemChange>? CompareWithPrevMonth(int year, int month)
        {
            var prev = new DateTime(year, month, 1).AddMonths(-1);
            return Compare(year, month, prev.Year, prev.Month);
        }

        /// <summary>작년 같은 달 대비</summary>
        public List<ItemChange>? CompareWithPrevYear(int year, int month)
        {
            return Compare(year, month, year - 1, month);
        }
    }
}
