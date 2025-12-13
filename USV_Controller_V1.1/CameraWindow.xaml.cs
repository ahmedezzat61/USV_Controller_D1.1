using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Shapes;
using Vlc.DotNet.Forms;

namespace USV_Controller_V1._1
{
    public partial class CameraWindow : Window
    {
        private VlcControl? _vlcPlayer;
        private readonly string _sourceUrl;

        public CameraWindow(string sourceUrl)
        {
            InitializeComponent();
            _sourceUrl = sourceUrl ?? throw new ArgumentNullException(nameof(sourceUrl));

            try
            {
                // ✅ Use relative path to bundled VLC
                string libPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "VLC");
                var libDir = new DirectoryInfo(libPath);

                if (!libDir.Exists)
                {
                    MessageBox.Show(
                        $"Bundled VLC not found at:\n{libPath}\n\n" +
                        $"Make sure VLC files are copied into Resources/VLC.",
                        "VLC Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Close();
                    return;
                }

                // Check for required DLLs
                var requiredDlls = new[] { "libvlc.dll", "libvlccore.dll" };
                foreach (var dll in requiredDlls)
                {
                    var dllPath = System.IO.Path.Combine(libPath, dll);
                    if (!File.Exists(dllPath))
                    {
                        MessageBox.Show(
                            $"Missing VLC library: {dll}\nPath: {libPath}\n\n" +
                            $"Ensure VLC files are bundled correctly.",
                            "Missing VLC Component",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        Close();
                        return;
                    }
                }

                // Initialize VLC player
                _vlcPlayer = new VlcControl();
                _vlcPlayer.BeginInit();
                _vlcPlayer.VlcLibDirectory = libDir;
                _vlcPlayer.EndInit();

                // ✅ Subscribe to VLC events
                _vlcPlayer.Playing += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (this.FindName("CameraStatus") is TextBlock status)
                        {
                            status.Text = "🟢 Streaming";
                            status.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
                        }
                        if (this.FindName("StreamingIndicator") is Ellipse indicator)
                        {
                            indicator.Fill = Brushes.LimeGreen;
                        }
                    });
                };

                _vlcPlayer.EncounteredError += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (this.FindName("CameraStatus") is TextBlock status)
                        {
                            status.Text = "❌ Error";
                            status.Foreground = Brushes.Red;
                        }
                        if (this.FindName("StreamingIndicator") is Ellipse indicator)
                        {
                            indicator.Fill = Brushes.Red;
                        }
                        MessageBox.Show(
                            $"Stream error. Possible causes:\n\n" +
                            $"• Wrong RTSP URL\n" +
                            $"• Authentication required\n" +
                            $"• Camera not streaming\n" +
                            $"• Network issue\n\n" +
                            $"Try the URL in VLC first to verify it works.",
                            "Stream Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                };

                _vlcPlayer.Stopped += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (this.FindName("CameraStatus") is TextBlock status)
                        {
                            status.Text = "⏸️ Stopped";
                            status.Foreground = Brushes.Orange;
                        }
                        if (this.FindName("StreamingIndicator") is Ellipse indicator)
                        {
                            indicator.Fill = Brushes.Gray;
                        }
                    });
                };

                // Host in WPF window
                vlcHost.Child = _vlcPlayer;

                // Start playback
                try
                {
                    _vlcPlayer.Play(new Uri(_sourceUrl));
                }
                catch
                {
                    try
                    {
                        _vlcPlayer.Play(_sourceUrl);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Failed to play stream:\n{ex.Message}\n\n" +
                            $"Stream URL: {_sourceUrl}\n\n" +
                            $"Check that:\n" +
                            $"• Camera is reachable at {_sourceUrl}\n" +
                            $"• Network connection is working\n" +
                            $"• RTSP port 554 is open",
                            "Playback Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Camera init failed: {ex.Message}\n\n" +
                    $"Common causes:\n" +
                    $"• Platform mismatch (x86 app with x64 VLC or vice versa)\n" +
                    $"• Missing VLC files in Resources/VLC\n" +
                    $"• Corrupted VLC files",
                    "Camera Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                if (_vlcPlayer != null)
                {
                    _vlcPlayer.Stop();
                    _vlcPlayer.Dispose();
                    _vlcPlayer = null;
                }
            }
            catch { }
        }
    }
}