using CommunityToolkit.Mvvm.ComponentModel;
using ManagementHouseFee.Models;
using ManagementHouseFee.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;

namespace ManagementHouseFee.ViewModels
{
    public partial class ItemComparisonViewModel : ObservableObject
    {
        private readonly DataService _dataService;
        private List<FeeRecord> _allRecords;
        private readonly FeeAnalysisService _analysis; // 비교 계산은 서비스에 위임

        // 날짜 선택
        public List<int> Years { get; } = new List<int>();
        public List<int> Months { get; } = Enumerable.Range(1, 12).ToList();

        [ObservableProperty] private int _selectedYear;
        [ObservableProperty] private int _selectedMonth;

        // 화면에 뿌려줄 분석 리스트
        [ObservableProperty]
        private ObservableCollection<ComparisonItem> _comparisonList;

        public ItemComparisonViewModel()
        {
            _dataService = new DataService();
            _allRecords = _dataService.Load();
            _analysis = new FeeAnalysisService(_allRecords);

            // 날짜 초기화 (현재 날짜)
            var now = DateTime.Now;
            for (int i = now.Year - 5; i <= now.Year + 1; i++) Years.Add(i);

            _selectedYear = now.Year;
            _selectedMonth = now.Month;

            AnalyzeData(); // 처음에 한 번 실행
        }

        // 연도나 월이 바뀌면 자동으로 분석 다시 실행
        partial void OnSelectedYearChanged(int value) => AnalyzeData();
        partial void OnSelectedMonthChanged(int value) => AnalyzeData();

        private void AnalyzeData()
        {
            ComparisonList = new ObservableCollection<ComparisonItem>();

            // 계산은 FeeAnalysisService가 담당하고, ViewModel은 화면 표시용 문자열·색상만 만든다.
            var vsPrevMonth = _analysis.CompareWithPrevMonth(SelectedYear, SelectedMonth);
            var vsPrevYear = _analysis.CompareWithPrevYear(SelectedYear, SelectedMonth);

            // 선택한 달의 데이터가 없으면 아무것도 안 함
            if (vsPrevMonth == null || vsPrevYear == null) return;

            for (int i = 0; i < vsPrevMonth.Count; i++)
            {
                var m = vsPrevMonth[i];
                var y = vsPrevYear[i]; // 두 리스트 모두 이번 달 항목 순서 그대로

                ComparisonList.Add(new ComparisonItem
                {
                    Name = m.Name,
                    CurrentAmount = m.Current,
                    PrevMonthMessage = GetComparisonString(m, "지난달"),
                    PrevMonthColor = GetColor(m),
                    PrevYearMessage = GetComparisonString(y, "작년"),
                    PrevYearColor = GetColor(y)
                });
            }
        }

        // "000원 (00%) 증가" 문자열 만들어주는 함수
        private string GetComparisonString(ItemChange c, string targetName)
        {
            switch (c.Status)
            {
                case CompareStatus.NoBaseRecord:
                    return $"{targetName} 데이터 없음";
                case CompareStatus.NewItem:
                    return $"{targetName}에는 없던 항목";
                case CompareStatus.Same:
                    return $"{targetName}과 동일";
            }

            double diff = c.Diff ?? 0;
            string sign = diff > 0 ? "증가 🔺" : "감소 🔻";
            string diffText = Math.Abs(diff).ToString("N0");

            // 이전 값이 0원이면 비율을 낼 수 없으므로 금액만 표시
            if (c.Rate is null)
                return $"{targetName} 0원 대비 {diffText}원 {sign}";

            return $"{targetName} 대비 {diffText}원 ({c.Rate.Value:P1}) {sign}";
        }

        // 색상 결정 (증가는 빨강, 감소는 파랑, 비교 불가·동일은 회색)
        private Brush GetColor(ItemChange c)
        {
            return c.Status switch
            {
                CompareStatus.Increased => Brushes.Red,
                CompareStatus.Decreased => Brushes.Blue,
                _ => Brushes.Gray
            };
        }
    }
}