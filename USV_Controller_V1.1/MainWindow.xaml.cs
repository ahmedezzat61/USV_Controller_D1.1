using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SharpDX.DirectInput;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Policy;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace USV_Controller_V1._1
{
    public partial class MainWindow : System.Windows.Window
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

        // ✅ DirectInput objects
        private DirectInput? _directInput;
        private Joystick? _joystick;
        private JoystickState _joystickState;
        private bool _firstPacketSent = false;
        private bool _throttleZeroed = false;

        // ✅ Pre-connection timer to poll joystick buttons
        private readonly System.Timers.Timer _joystickPollTimer;
        private bool _button6WasPressed = false;
        private bool _button4WasPressed = false;  // Theme toggle button
        private readonly DispatcherTimer _linkTimer;
        private bool _linkOn = false;
        private bool _isScanning = false;           // ✅ NEW: Prevent overlapping scans
        private string _espBuffer = "";

        private readonly System.Timers.Timer _pingTimer;
        private bool _isReachable = false;

        // ✅ NEW: ESP feedback timeout tracking
        private DateTime _lastEspFeedbackTime = DateTime.MinValue;
        private readonly System.Timers.Timer _espStatusTimer;
        private const int ESP_FEEDBACK_TIMEOUT_MS = 2000; // 2 seconds

        // ✅ NEW: ESP32 Groundstation Door Scanning
        private SerialPort? _espPort;
        private readonly System.Timers.Timer _espScanTimer;
        private bool _isEspConnected = false;


        private const string CameraIp = "192.168.0.50";
        private const int CameraRtspPort = 554;
        private const int CameraOnvifPort = 2020;
        private const string CameraUsername = "SMBSMB";
        private const string CameraPassword = "smbsmbsmb";
        private const string CameraRtspPath = "/stream1";

        private TapoPtzController? _ptzController;

        private void OpenCameraWindow()
        {
            string rtspUrl;

            if (!string.IsNullOrEmpty(CameraUsername) && !string.IsNullOrEmpty(CameraPassword))
            {
                rtspUrl = $"rtsp://{CameraUsername}:{CameraPassword}@{CameraIp}:{CameraRtspPort}{CameraRtspPath}";
            }
            else
            {
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

        private void InitializeJoystick()
        {
            try
            {
                _directInput = new DirectInput();

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
                        ResetJoystickSafety();
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"⚠️ Joystick cleanup error: {ex.Message}");
            }
        }

        private void ResetJoystickSafety()
        {
            _firstPacketSent = false;
            _throttleZeroed = false;
            _button6WasPressed = false;
            _button4WasPressed = false;
        }

        // ✅ NEW: Update ESP LED indicators
        private void UpdateEspLeds(bool relay1, bool relay2, bool relay3, bool relay4, bool relay5)
        {
            Dispatcher.Invoke(() =>
            {
                // Update LED colors based on relay states
                EspRelay1Led.Fill = relay1 ? Brushes.LimeGreen : Brushes.Red;
                EspRelay2Led.Fill = relay2 ? Brushes.LimeGreen : Brushes.Red;
                EspRelay3Led.Fill = relay3 ? Brushes.LimeGreen : Brushes.Red;
                EspRelay4Led.Fill = relay4 ? Brushes.LimeGreen : Brushes.Red;
                if (relay5 == true)
                {
                    Log("Watchdog activated, Arduino is restarting... ");
                }
                
            });
        }

        // ✅ NEW: Reset ESP LEDs to gray (no feedback)
        private void ResetEspLeds()
        {
            Dispatcher.Invoke(() =>
            {
                EspRelay1Led.Fill = Brushes.Gray;
                EspRelay2Led.Fill = Brushes.Gray;
                EspRelay3Led.Fill = Brushes.Gray;
                EspRelay4Led.Fill = Brushes.Gray;
                //EspRelay5Led.Fill = Brushes.Gray;
            });
        }

        public MainWindow()
        {
            InitializeComponent();

            _monitor = new MonitorWindow();
            _monitor.Show();
            InitializeJoystick();

            SetupRpmChart(RpmPieLeft);
            SetupRpmChart(RpmPieRight);

            // Subscribe to theme changes
            ThemeManager.ThemeChanged += () =>
            {
                UpdateGaugeTheme(ThemeManager.IsDarkTheme);
            };

            IntializedRpm(0);
            FeedbackRpm(0);

            _timer = new System.Timers.Timer(100);
            _timer.Elapsed += Timer_Elapsed;

            _joystickPollTimer = new System.Timers.Timer(100);
            _joystickPollTimer.Elapsed += JoystickPollTimer_Elapsed;
            _joystickPollTimer.Start();

            // ✅ NEW: ESP status timer to check for timeout
            _espStatusTimer = new System.Timers.Timer(500); // Check every 500ms
            _espStatusTimer.Elapsed += EspStatusTimer_Elapsed;
            _espStatusTimer.Start();

            // ✅ NEW: ESP32 Continuous Scan Timer
            _espScanTimer = new System.Timers.Timer(2000); // Scan every 2 seconds
            _espScanTimer.Elapsed += EspScanTimer_Elapsed;
            _espScanTimer.Start();


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
                string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "OpenCPN", "opencpn.exe");
                Process.Start(exePath);
                Log("🗺️ OpenCPN launched successfully");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Could not start OpenCPN: {ex.Message}");
            }
        }

        // ✅ NEW: ESP status timeout check
        private void EspStatusTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (_connected && _lastEspFeedbackTime != DateTime.MinValue)
            {
                var timeSinceLastFeedback = (DateTime.Now - _lastEspFeedbackTime).TotalMilliseconds;

                if (timeSinceLastFeedback > ESP_FEEDBACK_TIMEOUT_MS)
                {
                    // No feedback received for 2 seconds - set LEDs to gray
                    ResetEspLeds();
                }
            }
            else if (!_connected)
            {
                // Not connected - ensure LEDs are gray
                ResetEspLeds();
            }
        }

        private void JoystickPollTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (_joystick != null && _joystickState != null)
            {
                try
                {
                    _joystick.Poll();
                    _joystick.GetCurrentState(ref _joystickState);

                    bool button6Pressed = _joystickState.Buttons[6];
                    bool button4Pressed = _joystickState.Buttons[4];  // Theme toggle

                    // Button 6 - Connect/Disconnect
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

                    // Button 4 - Toggle Day/Night Theme
                    if (button4Pressed && !_button4WasPressed)
                    {
                        _button4WasPressed = true;

                        Dispatcher.Invoke(() =>
                        {
                            try 
                            {
                                ThemeManager.ToggleTheme();
                                string mode = ThemeManager.IsDarkTheme ? "Night Mode 🌙" : "Day Mode ☀️";
                                Log($"🎮 Button 5 pressed - Switched to {mode}");
                            }
                            catch (Exception ex)
                            {
                                Log($"❌ Theme Error: {ex.Message}");
                            }
                        });
                    }
                    else if (!button4Pressed)
                    {
                        _button4WasPressed = false;
                    }
                }
                catch (SharpDX.SharpDXException)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_joystick != null)
                        {
                            Log("⚠️ Joystick unplugged - attempting reconnection...");
                        }
                    });

                    try
                    {
                        _joystick?.Unacquire();
                        _joystick?.Dispose();
                    }
                    catch { }

                    _joystick = null;
                    _joystickState = null;

                    AttemptJoystickReconnect();
                }
                catch (Exception)
                {
                    // Ignore other errors
                }
            }
            else
            {
                AttemptJoystickReconnect();
            }
        }

        private void AttemptJoystickReconnect()
        {
            try
            {
                if (_directInput == null)
                {
                    _directInput = new DirectInput();
                }

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
            this.Left = 0;
            this.Top = 0;
            this.Width = 660;
            this.Height = 730;

            Log("✅ USV Controller initialized and ready.");
            Log("🎮 Press Button 7 to Connect/Disconnect");
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
            _espStatusTimer?.Stop();
            _espScanTimer?.Stop();

            if (_espPort != null && _espPort.IsOpen)
            {
                try { _espPort.Close(); } catch { }
            }


            try { usvClient?.Close(); } catch { }
            try { camClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _ptzController?.Dispose(); } catch { }

            CleanupJoystick();

            if (_monitor != null)
                _monitor.Close();

            Log("👋 Application closed");
        }

        private void Log(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                _monitor?.AddLog($"[{DateTime.Now:HH:mm:ss}] {msg}");
            });
        }

        private void UpdatePacket(string text)
        {
            _monitor?.UpdatePacket(text);
        }

        // =========================================================
        // ✅ NEW: Continuous SCANNING & DATA HANDLING for ESP32
        // =========================================================

        private void EspScanTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // ✅ FIX 1: Prevent overlapping scans
            if (_isScanning)
            {
                return; // Skip this scan cycle if already scanning
            }

            // If already connected, verify health
            if (_isEspConnected)
            {
                if (_espPort == null || !_espPort.IsOpen)
                {
                    _isEspConnected = false;
                    Dispatcher.Invoke(() =>
                    {
                        GroundStationDoorLed.Fill = Brushes.Gray;
                        Log("⚠️ ESP32 Serial disconnected unexpectedly.");
                    });
                }
                return;
            }

            // ✅ Set scanning flag
            _isScanning = true;

            try
            {
                string[] ports = SerialPort.GetPortNames();
                bool foundEsp = false;

                Log($"🔍 Scanning all COM port(s)...");

                foreach (string portName in ports)
                {
                    // Skip virtual COM ports
                    if (portName.StartsWith("CNC") || portName.StartsWith("COM0"))
                    {
                        continue;
                    }

                    //Log($"📡 Checking {portName}...");

                    if (TryConnectToEsp(portName))
                    {
                        _isEspConnected = true;
                        foundEsp = true;
                        Dispatcher.Invoke(() =>
                        {
                            GroundStationDoorLed.Fill = Brushes.Yellow;
                            Log($"✅ ESP32 Connected on {portName} - Monitoring for card events...");
                        });
                        break;
                    }
                }

                if (!foundEsp)
                {
                    Dispatcher.Invoke(() =>
                    {
                        GroundStationDoorLed.Fill = Brushes.Gray;
                        Log("⚠️ ESP32 NOT CONNECTED - RFID system not found on any COM port");
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"❌ ESP Scan Error: {ex.Message}");
                Dispatcher.Invoke(() => GroundStationDoorLed.Fill = Brushes.Gray);
            }
            finally
            {
                // ✅ Always release the lock
                _isScanning = false;
            }
        }

        private bool TryConnectToEsp(string portName)
        {
            SerialPort? testPort = null;
            try
            {
                testPort = new SerialPort(portName, 115200)
                {
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadTimeout = 400,  // Shorter timeout
                    WriteTimeout = 400
                };

                testPort.Open();

                // Wait for existing transmission
                System.Threading.Thread.Sleep(200);

                // Read any available data
                string response = "";
                int attempts = 0;

                while (attempts < 5) // Reduced from 8 to 5 for faster scanning
                {
                    if (testPort.BytesToRead > 0)
                    {
                        response += testPort.ReadExisting();
                    }
                    System.Threading.Thread.Sleep(80);
                    attempts++;

                    if (response.Length > 10) break;
                }

                // Log preview
                if (!string.IsNullOrWhiteSpace(response))
                {
                    string preview = response.Length > 50 ? response.Substring(0, 50) + "..." : response;
                    Log($"   ↳ {portName}: '{preview.Replace("\r", "").Replace("\n", " ")}'");
                }

                // Check for ESP32 RFID system signatures
                bool isEspRfid = response.Contains("RFID") ||
                                 response.Contains("Buzzer") ||
                                 response.Contains("Ready") ||
                                 response.Contains("CARD_IN") ||
                                 response.Contains("CARD_OUT") ||
                                 response.Contains("Allowed UID") ||
                                 response.Contains("ets Jul") ||  // Boot message
                                 response.Contains("rst:0x");     // Reset info

                if (isEspRfid)
                {
                    Log($"✅ {portName} - ESP32 RFID System identified!");

                    // Keep connection and attach handler
                    _espPort = testPort;
                    _espPort.DataReceived += EspPort_DataReceived;
                    return true;
                }

                // Not our device
                testPort.Close();
                testPort.Dispose();
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                try { testPort?.Close(); } catch { }
                try { testPort?.Dispose(); } catch { }
                return false;
            }
            catch (TimeoutException)
            {
                try { testPort?.Close(); } catch { }
                try { testPort?.Dispose(); } catch { }
                return false;
            }
            catch (Exception)
            {
                try { testPort?.Close(); } catch { }
                try { testPort?.Dispose(); } catch { }
                return false;
            }
        }

        private void EspPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_espPort == null || !_espPort.IsOpen) return;

            try
            {
                string newData = _espPort.ReadExisting();
                if (string.IsNullOrWhiteSpace(newData)) return;

                _espBuffer += newData;

                // ✅ Process all CARD_IN occurrences
                while (_espBuffer.Contains("CARD_IN"))
                {
                    int index = _espBuffer.IndexOf("CARD_IN");
                    _espBuffer = _espBuffer.Substring(index + 7);
                    _groundStationCard = 1;
                    Log($"📥 ESP32: CARD_IN");
                    Dispatcher.Invoke(() =>
                    {
                        GroundStationDoorLed.Fill = Brushes.LimeGreen;
                        Log("🟢 Ground Station Door: CARD DETECTED - Access Granted");
                    });
                }

                // ✅ Process all CARD_OUT occurrences  
                while (_espBuffer.Contains("CARD_OUT"))
                {
                    int index = _espBuffer.IndexOf("CARD_OUT");
                    _espBuffer = _espBuffer.Substring(index + 8);
                    _groundStationCard = 0;
                    Log($"📥 ESP32: CARD_OUT");
                    Dispatcher.Invoke(() =>
                    {
                        GroundStationDoorLed.Fill = Brushes.Red;
                        Log("🔴 Ground Station Door: CARD REMOVED - Access Denied");
                    });
                }

                // Clear buffer if too large
                if (_espBuffer.Length > 500) _espBuffer = "";
            }
            catch (Exception ex)
            {
                Log($"❌ ESP32 Read Error: {ex.Message}");

                try { _espPort?.Close(); _espPort?.Dispose(); } catch { }

                _espPort = null;
                _espBuffer = "";
                _isEspConnected = false;

                Dispatcher.Invoke(() =>
                {
                    GroundStationDoorLed.Fill = Brushes.Gray;
                    Log("⚠️ ESP32 Disconnected due to error");
                });
            }
        }

    }
}