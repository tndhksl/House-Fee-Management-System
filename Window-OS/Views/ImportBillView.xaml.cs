using ManagementHouseFee.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ManagementHouseFee.Views
{
    /// <summary>
    /// 드래그앤드롭은 마우스 이벤트라 화면(View)에서 받고, 파일 경로만 ViewModel에 넘긴다.
    /// </summary>
    public partial class ImportBillView : UserControl
    {
        private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        private static readonly Brush NormalBack = Brushes.White;
        private static readonly Brush ActiveBack = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFF));

        public ImportBillView()
        {
            InitializeComponent();
        }

        private ImportBillViewModel? Vm => DataContext as ImportBillViewModel;

        private void DropZone_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            DropBorder.Stroke = ActiveBorder;
            DropZone.Background = ActiveBack;
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            ResetDropZone();
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                Vm?.AddFiles(files);
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            Vm?.SelectFilesCommand.Execute(null);
        }

        private void ResetDropZone()
        {
            DropBorder.Stroke = NormalBorder;
            DropZone.Background = NormalBack;
        }
    }
}
