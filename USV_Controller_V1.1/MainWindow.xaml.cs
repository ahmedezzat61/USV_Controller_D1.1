using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SharpDX.XInput;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms;

namespace USV_Controller_V1._1
{
    public partial class MainWindow : Window
    {
        // --- NEW: monitor window instance ---
        private MonitorWindow _monitor;

        private UdpClient? usvClient;
        private UdpClient? camClient;
        private IPEndPoint? usvEndpoint;
        private IPEndPoint? camEndpoint;

        private UdpClient? _listener;
        private bool _connected = false;
        private readonly System.Timers.Timer _timer;
        private readonly Controller _controller;
        private Gamepad _gamepad;
        private readonly DispatcherTimer _linkTimer;
        private bool _linkOn = false;

        private double X = 0;


        private readonly System.Timers.Timer _pingTimer;
        private bool _isReachable = false;

        private const string CameraIp = "192.168.0.50";
        private const int CameraRtspPort = 554;
        private const int CameraOnvifPort = 2020;
        private const string CameraUsername = "SMBSMB";
        private const string CameraPassword = "smbsmbsmb";
        private const string CameraRtspPath = "/stream2";

        private TapoPtzController? _ptzController;

        private void OpenCameraWindow()
        {
            // ✅ Build RTSP URL with authentication
            string rtspUrl;

            if (!string.IsNullOrEmpty(CameraUsername) && !string.IsNullOrEmpty(CameraPassword))
            {
                // With authentication
                rtspUrl = $"rtsp://{CameraUsername}:{CameraPassword}@{CameraIp}:{CameraRtspPort}{CameraRtspPath}";
            }
            else
            {
                // Without authentication
                rtspUrl = $"rtsp://{CameraIp}:{CameraRtspPort}{CameraRtspPath}";
            }

            Log($"📹 Opening camera stream...");
            Log($" IP: {CameraIp}");
            Log($" Port: {CameraRtspPort}");
            Log($" Path: {CameraRtspPath}");
            Log($" Auth: {(!string.IsNullOrEmpty(CameraUsername) ? "Yes" : "No")}");

            try
            {

                var camWin = new CameraWindow(rtspUrl)
                {
                    Owner = this
                };

                camWin.Show();
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to open camera window: {ex.Message}");

                System.Windows.MessageBox.Show(
                    $"Camera window failed to open:\n{ex.Message}\n\n" +
                    $"📍 Troubleshooting Steps:\n\n" +
                    $"1. Test URL in VLC first:\n" +
                    $"   {rtspUrl}\n\n" +
                    $"2. Check camera web interface:\n" +
                    $"   http://{CameraIp}\n\n" +
                    $"3. Verify:\n" +
                    $"   • Camera is ON and reachable\n" +
                    $"   • RTSP is enabled on camera\n" +
                    $"   • Username/password are correct\n" +
                    $"   • RTSP path matches your camera model",
                    "Camera Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            // --------------------------
            // NEW — open monitor window
            // --------------------------
            _monitor = new MonitorWindow();
            _monitor.Show();

            SetupRpmChart(RpmPieLeft);
            SetupRpmChart(RpmPieRight);

            IntializedRpm(0);
            FeedbackRpm(0);

            _timer = new System.Timers.Timer(100);
            _timer.Elapsed += Timer_Elapsed;

            _controller = new Controller(UserIndex.One);
            Log(_controller.IsConnected ? "🎮 Xbox controller connected." : "⚠️ No Xbox controller detected!");

            _linkTimer = new DispatcherTimer();
            _linkTimer.Interval = TimeSpan.FromMilliseconds(400);
            _linkTimer.Tick += (s, e) =>
            {
                if (!_connected)
                {
                    LinkIndicator.Fill = Brushes.Gray;
                    LinkText.Text = "Disconnected";
                    return;
                }

                if (_isReachable)
                {
                    _linkOn = !_linkOn;
                    LinkIndicator.Fill = _linkOn ? Brushes.LimeGreen : Brushes.DarkGreen;
                    LinkText.Text = "Good";
                }
                else
                {
                    LinkIndicator.Fill = Brushes.Red;
                    LinkText.Text = "Lost";
                }
            };
            _linkTimer.Start();

            _pingTimer = new System.Timers.Timer(2000);
            _pingTimer.Elapsed += PingTimer_Elapsed;

            _ptzController = new TapoPtzController(CameraIp, CameraOnvifPort, CameraUsername, CameraPassword);
            Log($"🎥 Tapo PTZ controller initialized (Port {CameraOnvifPort})");

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;

            try
            {
                string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "OpenCPN", "opencpn.exe");
                Process.Start(exePath);
                Log("🗺️ OpenCPN launched successfully");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Could not start OpenCPN: {ex.Message}");
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            this.Left = 630;      // Adjust for your monitor
            this.Top = 0;
            this.Width = 660;    // Your preferred width
            this.Height = 680;    // Your preferred height

            Log("✅ USV Controller initialized and ready.");
            this.Activate();
            this.Focus();

            try
            {
                OpenCameraWindow();
                Log("📹 Camera window opened");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Camera error: {ex.Message}");
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            Log("🛑 Shutting down...");

            _timer?.Stop();
            _pingTimer?.Stop();
            _linkTimer?.Stop();

            try { usvClient?.Close(); } catch { }
            try { camClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _ptzController?.Dispose(); } catch { }

            if (_monitor != null)
                _monitor.Close();

            Log("👋 Application closed");
        }

        // -------------------------------------------------
        // MODIFIED: Log now also writes to MonitorWindow
        // -------------------------------------------------
        private void Log(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                _monitor?.AddLog($"[{DateTime.Now:HH:mm:ss}] {msg}");
            });
        }

        // -------------------------------------------------
        // MODIFIED: packet preview forwarder
        // -------------------------------------------------
        private void UpdatePacket(string text)
        {
            _monitor?.UpdatePacket(text);
        }

        // ---------------------------------------
        // KEEP ALL YOUR OTHER METHODS AS THEY ARE
        // ---------------------------------------

    }
}
