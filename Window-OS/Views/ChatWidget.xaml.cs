using System.Windows.Controls;

namespace ManagementHouseFee.Views
{
    public partial class ChatWidget : UserControl
    {
        public ChatWidget()
        {
            InitializeComponent();
        }

        /// <summary>새 말풍선이 생기거나 답변이 길어지면 맨 아래로 스크롤</summary>
        private void MessageScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange > 0)
                MessageScroll.ScrollToEnd();
        }
    }
}
