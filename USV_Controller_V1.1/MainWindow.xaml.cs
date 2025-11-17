using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SharpDX.XInput;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// Add this to your MainWindow.xaml.cs (partial class)

namespace USV_Controller_V1._1
{
    public partial class MainWindow : Window


    {
        private UdpClient? usvClient;
        private UdpClient? camClient;
        private IPEndPoint? usvEndpoint;
        private IPEndPoint? camEndpoint;


        private UdpClient? _listener;
        private bool _connected = false;
        private readonly Timer _timer;
        private readonly Controller _controller;
        private Gamepad _gamepad;
        private readonly DispatcherTimer _linkTimer;
        private bool _linkOn = false;

        // █ FIX #1 – X variable declared as class member to persist across loop iterations
        private double X = 0;

        // █ FIX #3 – Continuous ping monitoring
        private readonly Timer _pingTimer;
        private bool _isReachable = false;
        // ✅ Camera configuration (FROM AGENTDVR - CONFIRMED WORKING)
        private const string CameraIp = "192.168.0.50";
        private const int CameraRtspPort = 554;
        private const int CameraOnvifPort = 2020;  // ✅ ONVIF PTZ control port

        // 🔐 Camera authentication (FROM YOUR AGENTDVR SCREENSHOT)
        private const string CameraUsername = "SMBSMB";
        private const string CameraPassword = "smbsmbsmb";

        // ✅ RTSP path (FROM AGENTDVR "Get Video URLs")
        private const string CameraRtspPath = "/stream2";  // ✅ CONFIRMED!

        // ✅ ONVIF PTZ Controller
        private OnvifPtzController? _ptzController;

        /// <summary>
        /// Opens the camera window with correct RTSP URL
        /// </summary>
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
            Log($"   IP: {CameraIp}");
            Log($"   Port: {CameraRtspPort}");
            Log($"   Path: {CameraRtspPath}");
            Log($"   Auth: {(!string.IsNullOrEmpty(CameraUsername) ? "Yes" : "No")}");

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
                MessageBox.Show(
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
        // █ Charts Manager for OpenCPN Integration
        //private ChartsManager _chartsManager;

        public MainWindow()
        {
            InitializeComponent();

            // ✅ Setup gauges first
            SetupRpmChart(RpmPieLeft);
            SetupRpmChart(RpmPieRight);

            IntializedRpm(0);
            FeedbackRpm(0);

            // ✅ Setup timers
            _timer = new Timer(100);
            _timer.Elapsed += Timer_Elapsed;

            _controller = new Controller(UserIndex.One);
            Log(_controller.IsConnected ? "🎮 Xbox controller connected." : "⚠️ No Xbox controller detected!");

            // ✅ Setup link indicator timer
            _linkTimer = new DispatcherTimer();
            _linkTimer.Interval = TimeSpan.FromMilliseconds(400);
            _linkTimer.Tick += (s, e) =>
            {
                if (!_connected)
                {
                    LinkIndicator.Fill = Brushes.Gray;
                    LinkText.Text = "Link: Disconnected";
                    return;
                }

                if (_isReachable)
                {
                    _linkOn = !_linkOn;
                    LinkIndicator.Fill = _linkOn ? Brushes.LimeGreen : Brushes.DarkGreen;
                    LinkText.Text = "Link: Good";
                }
                else
                {
                    LinkIndicator.Fill = Brushes.Red;
                    LinkText.Text = "Link: Lost";
                }
            };
            _linkTimer.Start();

            // ✅ Setup ping timer
            _pingTimer = new Timer(2000);
            _pingTimer.Elapsed += PingTimer_Elapsed;

            // ✅ Initialize ONVIF PTZ controller for camera
            _ptzController = new OnvifPtzController(CameraIp, CameraOnvifPort, CameraUsername, CameraPassword);
            Log("🎥 ONVIF PTZ controller initialized");

            // ✅ Handle window events
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;

            // ✅ Launch OpenCPN (MainWindow will be shown by App.xaml StartupUri)
            try
            {
                System.Diagnostics.Process.Start(@"C:\Program Files (x86)\OpenCPN2\opencpn.exe");
                Log("🗺️ OpenCPN launched successfully");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Could not start OpenCPN: {ex.Message}");
                MessageBox.Show("Could not start OpenCPN: " + ex.Message, "OpenCPN Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // ✅ Open camera window when MainWindow loads (moved to Loaded event)
            // OpenCameraWindow() will be called in MainWindow_Loaded instead
        }

        /// <summary>
        /// Called when main window finishes loading
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Log("✅ USV Controller initialized and ready");

            // Bring MainWindow to front
            this.Activate();
            this.Focus();

            // Open camera window now that MainWindow is fully loaded
            try
            {
                OpenCameraWindow();
                Log("📹 Camera window opened");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Camera window error: {ex.Message}");
                MessageBox.Show($"Camera failed to open: {ex.Message}", "Camera Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Called when main window is closing
        /// </summary>
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            Log("🛑 Shutting down application...");

            // Stop timers
            _timer?.Stop();
            _pingTimer?.Stop();
            _linkTimer?.Stop();

            // Close UDP connections
            try { usvClient?.Close(); } catch { }
            try { camClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }

            // Dispose ONVIF PTZ controller
            try { _ptzController?.Dispose(); } catch { }

            _connected = false;

            // Optional: Close OpenCPN when app closes
            try
            {
                var opencpnProcesses = System.Diagnostics.Process.GetProcessesByName("opencpn");
                foreach (var proc in opencpnProcesses)
                {
                    proc.Kill();
                    Log("🗺️ OpenCPN closed");
                }
            }
            catch { }

            Log("👋 Application closed");
        }

        private void Log(string msg)
                {
                    Dispatcher.Invoke(() =>
                    {
                        RawLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                        RawLog.ScrollToEnd();
                    });
                }

                // ═══════════════════════════════════════════════════════════════
                // NOTE: Add your other methods here:
                // - SetupRpmChart()
                // - IntializedRpm()
                // - FeedbackRpm()
                // - Timer_Elapsed()
                // - ConnectButton_Click()
                // - DisconnectButton_Click()
                // - StartListening()
                // - And any other methods from your original MainWindow
                // ═══════════════════════════════════════════════════════════════
            }
        }