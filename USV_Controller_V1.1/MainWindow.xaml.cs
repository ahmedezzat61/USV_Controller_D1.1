using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SharpDX.DirectInput; // ✅ CHANGED: DirectInput instead of XInput
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
        private UdpClient? espClient;
        private IPEndPoint? espEndpoint;

        private UdpClient? _listener;
        private bool _connected = false;
        private readonly System.Timers.Timer _timer;

        // ✅ CHANGED: DirectInput objects instead of XInput
        private DirectInput? _directInput;
        private Joystick? _joystick;
        private JoystickState _joystickState;
        private bool _firstPacketSent = false;
        private bool _throttleZeroed = false;
        // ✅ NEW: Pre-connection timer to poll joystick buttons
        private readonly System.Timers.Timer _joystickPollTimer;
        private bool _button6WasPressed = false;  // Track button state to prevent repeated triggers
        private readonly DispatcherTimer _linkTimer;
        private bool _linkOn = false;

        private readonly System.Timers.Timer _pingTimer;
        private bool _isReachable = false;

        private const string CameraIp = "192.168.0.50";
        private const int CameraRtspPort = 554;
        private const int CameraOnvifPort = 2020;
        private const string CameraUsername = "SMBSMB";
        private const string CameraPassword = "smbsmbsmb";
        private const string CameraRtspPath = "/stream1";

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

        // ✅ NEW: Initialize DirectInput Joystick
        // ✅ IMPROVED: Initialize DirectInput Joystick with comprehensive detection
        // ✅ IMPROVED: Initialize DirectInput Joystick with comprehensive detection
        // ✅ CLEAN & MINIMAL: Initialize DirectInput Joystick
        private void InitializeJoystick()
        {
            try
            {
                _directInput = new DirectInput();

                // Find first game controller (covers all game device types)
                var joystickGuid = Guid.Empty;
                string deviceName = "";

                foreach (var device in _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
                {
                    joystickGuid = device.InstanceGuid;
                    deviceName = device.ProductName;
                    break;
                }

                if (joystickGuid == Guid.Empty)
                {
                    Log("⚠️ No joystick detected!");
                    return;
                }

                _joystick = new Joystick(_directInput, joystickGuid);
                _joystick.Properties.BufferSize = 128;
                _joystick.Acquire();
                _joystickState = new JoystickState();

                Log($"🎮 {deviceName} connected successfully.");
            }
            catch (Exception ex)
            {
                Log($"❌ Joystick initialization failed: {ex.Message}");
            }
        }

        // ✅ NEW: Update joystick state (replaces gamepad polling)
        // ✅ IMPROVED: Update joystick state with disconnect detection
        private void UpdateJoystickState()
        {
            if (_joystick != null && _joystickState != null)
            {
                try
                {
                    _joystick.Poll();
                    _joystick.GetCurrentState(ref _joystickState);
                }
                catch (SharpDX.SharpDXException)
                {
                    // Joystick disconnected during operation
                    Log("⚠️ Joystick disconnected during operation");

                    try
                    {
                        _joystick?.Unacquire();
                        _joystick?.Dispose();
                    }
                    catch { }

                    _joystick = null;
                    _joystickState = null;
                }
                catch (Exception)
                {
                    // Ignore other errors
                }
            }
        }

        // ✅ NEW: Clean up joystick
        private void CleanupJoystick()
        {
            try
            {
                _joystick?.Unacquire();
                _joystick?.Dispose();
                _directInput?.Dispose();
                Log("🎮 Joystick disconnected.");
            }

            catch (SharpDX.SharpDXException)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_joystick != null)
                    {
                        Log("⚠️ Joystick unplugged - attempting reconnection...");
                        ResetJoystickSafety(); // ✅ Reset safety flags
                    }
                });
            }

            catch (Exception ex)
            {
                Log($"⚠️ Joystick cleanup error: {ex.Message}");
            }
        }

        // ✅ NEW: Reset safety flags when joystick disconnects
        private void ResetJoystickSafety()
        {
            _firstPacketSent = false;
            _throttleZeroed = false;
            _button6WasPressed = false;
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

            // ✅ CHANGED: Initialize DirectInput joystick instead of XInput controller
            InitializeJoystick();

            // ✅ NEW: Joystick button poll timer (always running to detect Button 6)
            _joystickPollTimer = new System.Timers.Timer(100);  // Poll every 100ms
            _joystickPollTimer.Elapsed += JoystickPollTimer_Elapsed;
            _joystickPollTimer.Start();  // Start immediately

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

            //try
            //{
            //    string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "OpenCPN", "opencpn.exe");
            //    Process.Start(exePath);
            //    Log("🗺️ OpenCPN launched successfully");
            //}
            //catch (Exception ex)
            //{
            //    Log($"⚠️ Could not start OpenCPN: {ex.Message}");
            //}
        }

        // ✅ NEW: Poll joystick buttons even when not connected
        // ✅ ENHANCED: Poll joystick with disconnect/reconnect detection
        private void JoystickPollTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // Try to poll existing joystick
            if (_joystick != null && _joystickState != null)
            {
                try
                {
                    // Update joystick state
                    _joystick.Poll();
                    _joystick.GetCurrentState(ref _joystickState);

                    // Check if Button 6 is pressed
                    bool button6Pressed = _joystickState.Buttons[6];

                    // Detect button press (not held - only trigger once per press)
                    if (button6Pressed && !_button6WasPressed)
                    {
                        _button6WasPressed = true;

                        Dispatcher.Invoke(() =>
                        {
                            if (!_connected)
                            {
                                Log("🎮 Button 6 pressed - Connecting...");
                                ConnectButton_Click(null, null);
                            }
                            else
                            {
                                Log("🎮 Button 6 pressed - Disconnecting...");
                                DisconnectButton_Click(null, null);
                            }
                        });
                    }
                    else if (!button6Pressed)
                    {
                        _button6WasPressed = false;
                    }
                }
                catch (SharpDX.SharpDXException)
                {
                    // Joystick disconnected
                    Dispatcher.Invoke(() =>
                    {
                        if (_joystick != null) // Only log if we had a joystick
                        {
                            Log("⚠️ Joystick unplugged - attempting reconnection...");
                        }
                    });

                    // Clean up current joystick
                    try
                    {
                        _joystick?.Unacquire();
                        _joystick?.Dispose();
                    }
                    catch { }

                    _joystick = null;
                    _joystickState = null;

                    // Attempt to reconnect
                    AttemptJoystickReconnect();
                }
                catch (Exception)
                {
                    // Other errors - ignore silently
                }
            }
            else
            {
                // No joystick connected - try to reconnect
                AttemptJoystickReconnect();
            }
        }

        // ✅ NEW: Attempt to reconnect joystick
        private void AttemptJoystickReconnect()
        {
            try
            {
                if (_directInput == null)
                {
                    _directInput = new DirectInput();
                }

                // Find first game controller
                var joystickGuid = Guid.Empty;
                string deviceName = "";

                foreach (var device in _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
                {
                    joystickGuid = device.InstanceGuid;
                    deviceName = device.ProductName;
                    break;
                }

                if (joystickGuid != Guid.Empty)
                {
                    // Found a joystick - connect to it
                    _joystick = new Joystick(_directInput, joystickGuid);
                    _joystick.Properties.BufferSize = 128;
                    _joystick.Acquire();
                    _joystickState = new JoystickState();

                    Dispatcher.Invoke(() =>
                    {
                        Log($"✅ Joystick reconnected: {deviceName}");
                    });
                }
            }
            catch (Exception)
            {
                // Silently fail - will retry on next poll
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
            _joystickPollTimer?.Stop();

            try { usvClient?.Close(); } catch { }
            try { camClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _ptzController?.Dispose(); } catch { }

            // ✅ NEW: Clean up joystick
            CleanupJoystick();

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