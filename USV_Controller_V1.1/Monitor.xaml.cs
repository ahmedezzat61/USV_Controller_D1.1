using System;
using System.Windows;

namespace USV_Controller_V1._1
{
    public partial class MonitorWindow : Window
    {
        public MonitorWindow()
        {
            InitializeComponent();
        }

        // --- Receive log text from MainWindow ---
        public void AddLog(string text)
        {
            Dispatcher.Invoke(() =>
            {
                RawLog.AppendText(text + "\n");
                RawLog.ScrollToEnd();
            });
        }

        // --- Receive packet preview text from MainWindow ---
        public void UpdatePacket(string text)
        {
            Dispatcher.Invoke(() =>
            {
                PacketPreview.Text = text;
            });
        }
    }
}
