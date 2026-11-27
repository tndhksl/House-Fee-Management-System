using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagementHouseFee.Models;
using ManagementHouseFee.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace ManagementHouseFee.ViewModels
{
    /// <summary>끌어다 놓거나 선택한 캡처 파일 1개</summary>
    public record DroppedFile(string Path)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>표의 한 줄: 고지서 항목 1개와 사용자가 고른 분류</summary>
    public partial class BillRowViewModel : ObservableObject
    {
        public string Name { get; }

        [ObservableProperty] private long _amount;               // 잘못 읽었으면 사용자가 고칠 수 있음
        [ObservableProperty] private string? _category;          // null이면 아직 미분류
        public bool WasUnknown { get; }                           // 매핑표에 없던 항목인지
        public string? MappedCategory { get; }                    // 매핑표가 정한 분류 (없으면 null)

        public BillRowViewModel(string name, long amount, string? mappedCategory, bool useBillName)
        {
            Name = name;
            _amount = amount;
            MappedCategory = mappedCategory;
            WasUnknown = mappedCategory == null;
            _category = useBillName ? name : mappedCategory;
        }
    }

    /// <summary>
    /// "고지서로 입력" 화면.
    /// 캡처 선택 → 전송 동의 → AI 추출 → 자동 점검 → 사용자가 표에서 확인·수정 → 저장
    /// </summary>
    public partial class ImportBillViewModel : ObservableObject
    {
        private readonly DataService _dataService = new();
        private readonly CategoryMapper _mapper = new();
        private readonly BillImporter? _importer;

        public ObservableCollection<DroppedFile> SelectedFiles { get; } = new();
        public ObservableCollection<BillRowViewModel> Rows { get; } = new();

        /// <summary>분류 선택지 = 앱에 등록된 항목 목록 (+ 고지서 항목명 그대로 저장 시 그 이름들)</summary>
        public ObservableCollection<string> Categories { get; } = new();

        private List<string> _appItems = new();

        /// <summary>
        /// true: 고지서 항목명 그대로 앱 항목으로 저장 (새 이름은 앱 항목 목록에 자동 추가)
        /// false: 매핑표로 전기세·수도세·관리비 등에 묶어서 저장
        /// </summary>
        [ObservableProperty] private bool _useBillNames = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExtractCommand))]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        private int? _year;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        private int? _month;

        [ObservableProperty] private long? _statedTotal;
        [ObservableProperty] private string _statusMessage = "";
        [ObservableProperty] private string _sumCheckMessage = "";
        [ObservableProperty] private bool _isSumOk;
        [ObservableProperty] private string _notesText = "";
        [ObservableProperty] private string _warningText = "";

        public ImportBillViewModel()
        {
            _appItems = _dataService.LoadItems();
            foreach (var c in _appItems) Categories.Add(c);
            try
            {
                _importer = new BillImporter(ClaudeClient.FromEnvironment());
                StatusMessage = "같은 달 고지서 캡처를 여러 장 한 번에 넣을 수 있습니다. 겹친 화면은 자동으로 중복 제거됩니다.";
            }
            catch (InvalidOperationException ex)
            {
                StatusMessage = ex.Message + " 환경변수를 설정한 뒤 프로그램을 다시 실행하세요.";
            }
            SelectedFiles.CollectionChanged += (_, _) => ExtractCommand.NotifyCanExecuteChanged();
        }

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

        /// <summary>드래그앤드롭·파일 선택 공통 진입점. 이미지가 아닌 파일과 이미 넣은 파일은 건너뛴다.</summary>
        public void AddFiles(IEnumerable<string> paths)
        {
            int skipped = 0;
            foreach (var p in paths)
            {
                bool isImage = ImageExtensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant());
                if (!isImage || SelectedFiles.Any(f => f.Path == p)) { skipped++; continue; }
                SelectedFiles.Add(new DroppedFile(p));
            }
            if (skipped > 0) StatusMessage = $"이미지가 아니거나 이미 추가된 파일 {skipped}개는 건너뛰었습니다.";
        }

        [RelayCommand]
        private void SelectFiles()
        {
            var dialog = new OpenFileDialog
            {
                Title = "관리비 고지서 캡처 선택",
                Filter = "이미지 (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
        }

        [RelayCommand]
        private void RemoveFile(DroppedFile file) => SelectedFiles.Remove(file);

        [RelayCommand]
        private void ClearFiles()
        {
            SelectedFiles.Clear();
            ClearResult();
        }

        private bool CanExtract() => _importer != null && !IsBusy && SelectedFiles.Count > 0;

        [RelayCommand(CanExecute = nameof(CanExtract))]
        private async Task ExtractAsync()
        {
            // 지금까지는 '계산 결과만' 외부로 보냈지만, 이미지는 원본이 그대로 나간다 → 사용자 동의를 받는다.
            var consent = MessageBox.Show(
                "선택한 이미지가 Anthropic Claude API로 전송됩니다.\n" +
                "이름, 동·호수, 주소, 고객번호가 보이지 않는지 확인하세요.\n\n계속할까요?",
                "외부 전송 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (consent != MessageBoxResult.Yes) return;

            IsBusy = true;
            StatusMessage = "고지서를 읽는 중입니다...";
            ClearResult();

            try
            {
                var r = await _importer!.ExtractAsync(SelectedFiles.Select(f => f.Path).ToList());

                Year = r.Year;
                Month = r.Month;
                StatedTotal = r.StatedTotal;

                foreach (var item in r.Items)
                {
                    string? cat = _mapper.Map(item.Name);
                    if (cat != null && !_appItems.Contains(cat)) cat = null; // 앱에 없는 항목이면 사용자가 고르게
                    if (!Categories.Contains(item.Name)) Categories.Add(item.Name);
                    var row = new BillRowViewModel(item.Name, item.Amount, cat, UseBillNames);
                    row.PropertyChanged += OnRowChanged;
                    Rows.Add(row);
                }

                var notes = r.Notes.Concat(r.Conflicts.Select(c => "⚠ " + c)).ToList();
                NotesText = notes.Count == 0 ? "" : string.Join("\n", notes);
                StatusMessage = $"읽기 완료 · 토큰 입력 {r.InputTokens:N0} / 출력 {r.OutputTokens:N0}. 아래 표를 확인하고 저장하세요.";
                Recalculate();
            }
            catch (Exception ex)
            {
                StatusMessage = $"오류: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Recalculate();

        /// <summary>표가 바뀔 때마다 합계 점검·분류별 합계·저장 가능 여부를 다시 계산</summary>
        private void Recalculate()
        {
            long sum = Rows.Sum(r => r.Amount);

            if (StatedTotal is null)
            {
                IsSumOk = false;
                SumCheckMessage = $"합계 점검 불가: 고지서 총액을 읽지 못했습니다. 항목 합계 {sum:N0}원";
            }
            else
            {
                IsSumOk = sum == StatedTotal;
                SumCheckMessage = IsSumOk
                    ? $"✔ 합계 점검 통과: 항목 합계 {sum:N0}원 = 고지서 총액 {StatedTotal:N0}원"
                    : $"⚠ 합계 불일치: 항목 합계 {sum:N0}원 ≠ 고지서 총액 {StatedTotal:N0}원 (차이 {sum - StatedTotal:N0}원)";
            }

            int unknown = Rows.Count(r => r.Category == null);
            var warnings = new List<string>();
            if (unknown > 0) warnings.Add($"분류가 필요한 항목 {unknown}개");
            int added = Rows.Select(r => r.Category).Where(c => c != null && !_appItems.Contains(c)).Distinct().Count();
            if (added > 0) warnings.Add($"새 항목 {added}개가 앱 항목 목록에 추가됩니다");
            if (Year != null && Month != null && _dataService.Load().Any(x => x.Year == Year && x.Month == Month))
                warnings.Add($"{Year}년 {Month}월 기록이 이미 있습니다. 저장하면 덮어씁니다");
            WarningText = string.Join(" · ", warnings);

            SaveCommand.NotifyCanExecuteChanged();
        }

        private bool CanSave() =>
            Rows.Count > 0 && Rows.All(r => r.Category != null) && Year is >= 2000 and <= 2100 && Month is >= 1 and <= 12;

        [RelayCommand(CanExecute = nameof(CanSave))]
        private void Save()
        {
            if (StatedTotal != null && !IsSumOk)
            {
                var go = MessageBox.Show("합계가 고지서 총액과 다릅니다. 그래도 저장할까요?", "합계 불일치",
                                         MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (go != MessageBoxResult.Yes) return;
            }

            if (UseBillNames)
            {
                // 앱 항목 목록에 없는 이름을 자동으로 추가 → '내역 추가'·비교 화면에도 같은 이름으로 나온다
                var newNames = Rows.Select(r => r.Category!).Distinct().Where(n => !_appItems.Contains(n)).ToList();
                if (newNames.Count > 0)
                {
                    _appItems.AddRange(newNames);
                    _dataService.SaveItems(_appItems);
                }
            }
            else
            {
                // 사용자가 직접 분류한 항목은 매핑표에 저장 → 다음 고지서부터 자동 분류
                foreach (var row in Rows.Where(r => r.Category != r.MappedCategory))
                    _mapper.Remember(row.Name, row.Category!);
            }

            var record = new FeeRecord
            {
                Year = Year!.Value,
                Month = Month!.Value,
                Items = CategoryMapper.Aggregate(Rows.Select(r => (r.Category!, r.Amount)))
            };

            var all = _dataService.Load();
            all.RemoveAll(r => r.Year == record.Year && r.Month == record.Month);
            all.Add(record);
            _dataService.Save(all);

            MessageBox.Show($"{record.Year}년 {record.Month}월 관리비가 저장되었습니다.\n{record.Items.Count}개 항목 · 총 {record.TotalAmount:N0}원");
            StatusMessage = "저장했습니다.";
        }

        /// <summary>옵션을 바꾸면 표의 분류를 즉시 다시 채운다.</summary>
        partial void OnUseBillNamesChanged(bool value)
        {
            foreach (var row in Rows)
                row.Category = value ? row.Name : row.MappedCategory;
            Recalculate();
        }

        partial void OnYearChanged(int? value) => Recalculate();
        partial void OnMonthChanged(int? value) => Recalculate();

        private void ClearResult()
        {
            foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
            Rows.Clear();
            Year = null;
            Month = null;
            StatedTotal = null;
            SumCheckMessage = NotesText = WarningText = "";
        }
    }
}
