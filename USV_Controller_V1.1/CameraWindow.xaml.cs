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
                // ✅ Match your project architecture to VLC
                // If your project is x64, use 64-bit VLC path
                // If your project is x86, use 32-bit VLC path
                string libPath = Environment.Is64BitProcess
                    ? @"C:\Program Files\VideoLAN\VLC"          // 64-bit VLC
                    : @"C:\Program Files (x86)\VideoLAN\VLC";   // 32-bit VLC

                // Fallback check
                if (!Directory.Exists(libPath))
                {
                    libPath = Environment.Is64BitProcess
                        ? @"C:\Program Files (x86)\VideoLAN\VLC"
                        : @"C:\Program Files\VideoLAN\VLC";
                }

                var libDir = new DirectoryInfo(libPath);

                if (!libDir.Exists)
                {
                    MessageBox.Show(
                        $"VLC not found. Searched:\n" +
                        $"• C:\\Program Files\\VideoLAN\\VLC (64-bit)\n" +
                        $"• C:\\Program Files (x86)\\VideoLAN\\VLC (32-bit)\n\n" +
                        $"Please install VLC Media Player from:\n" +
                        $"https://www.videolan.org/vlc/",
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
                            $"Missing VLC library: {dll}\n" +
                            $"Path: {libPath}\n\n" +
                            $"Reinstall VLC Media Player.",
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

                // ✅ Subscribe to VLC events to detect stream status
                _vlcPlayer.Playing += (s, e) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (this.FindName("CameraStatus") is TextBlock status)
                        {
                            status.Text = "🟢 Streaming";
                            status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 201, 176));
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
                    // Fallback to string overload
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
                    $"• Missing VLC installation\n" +
                    $"• Corrupted VLC files\n\n" +
                    $"Try:\n" +
                    $"1. Reinstall VLC (64-bit version)\n" +
                    $"2. Set project Platform to x64",
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