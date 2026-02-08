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
                _cameraWindow = new CameraWindow(rtspUrl)
                {
                    Owner = this
                };

                _cameraWindow.Show();
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

        private CameraWindow? _cameraWindow;

        // ✅ P/Invoke for external window management (OpenCPN)
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
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

            // ✅ Arrange windows according to plan
            ArrangeWindows();

            // ✅ Set Startup
            try
            {
                SetStartup();
            }
            catch (Exception ex)
            {
                Log($"⚠️ Startup config error: {ex.Message}");
            }
        }

        private void SetStartup()
        {
            try
            {
                string rkPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(rkPath, true))
                {
                    if (rk != null)
                    {
                        var appName = "USV_Controller_V1.1";
                        var appPath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                        
                        // Check if value exists, if not, set it
                        var existingValue = rk.GetValue(appName);
                        if (existingValue == null || existingValue.ToString() != appPath)
                        {
                            rk.SetValue(appName, appPath);
                            Log("✅ Added to Windows Startup");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Could not add to startup: {ex.Message}");
            }
        }

        // ✅ P/Invoke for window style modification
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_POPUP = -2147483648; // 0x80000000

        private void ArrangeWindows()
        {
            // Get available screen dimensions (excluding taskbar)
            double screenWidth = SystemParameters.WorkArea.Width;
            double screenHeight = SystemParameters.WorkArea.Height;
            double halfWidth = screenWidth / 2.0;

            // 1. Position Main Window (Top Left - 50% Width, 75% Height)
            this.Left = 0;
            this.Top = 0;
            this.Width = halfWidth;
            this.Height = screenHeight * 0.65;

            // 2. Position Monitor Window (Bottom Left - 50% Width, 25% Height)
            if (_monitor != null)
            {
                _monitor.Left = 0;
                _monitor.Top = this.Height; // Starts exactly where Main ends
                _monitor.Width = halfWidth;
                _monitor.Height = screenHeight * 0.35; // Remaining vertical space
            }

            // 3. Position Camera Window (Top Right - 50% Width, 55% Height)
            if (_cameraWindow != null)
            {
                _cameraWindow.Left = halfWidth; // Starts at middle of screen
                _cameraWindow.Top = 0;
                _cameraWindow.Width = halfWidth;
                _cameraWindow.Height = screenHeight * 0.55; // 55% height
            }

            // 4. Position OpenCPN (Bottom Right - 50% Width, 45% Height)
            try
            {
                Process[] processes = Process.GetProcessesByName("opencpn");
                if (processes.Length > 0)
                {
                    IntPtr handle = processes[0].MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        // ✅ Remove Title Bar and Resize Frame
                        int style = GetWindowLong(handle, GWL_STYLE);
                        
                        // Remove Caption (Title bar) and ThickFrame (Resizing border)
                        // Verify we aren't completely breaking it, basically turning it into a borderless window
                        if ((style & WS_CAPTION) != 0 || (style & WS_THICKFRAME) != 0)
                        {
                            style &= ~(WS_CAPTION | WS_THICKFRAME);
                            SetWindowLong(handle, GWL_STYLE, style);
                        }

                        // Calculate logical position
                        double xLogical = halfWidth;
                        double yLogical = screenHeight * 0.55; // Starts at 55%
                        double wLogical = halfWidth;
                        double hLogical = screenHeight * 0.45; // Remaining 45%

                        // ✅ Convert to Physical Pixels for SetWindowPos (DPI Scaling)
                        double dpiScaleX = 1.0;
                        double dpiScaleY = 1.0;

                        // Retrieve DPI from the current window's PresentationSource
                        var source = PresentationSource.FromVisual(this);
                        if (source != null && source.CompositionTarget != null)
                        {
                            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
                        }

                        int x = (int)(xLogical * dpiScaleX);
                        int y = (int)(yLogical * dpiScaleY);
                        int w = (int)(wLogical * dpiScaleX);
                        int h = (int)(hLogical * dpiScaleY);

                        // FLAGS: 
                        // 0x0004 (SWP_NOZORDER) 
                        // 0x0040 (SWP_SHOWWINDOW)
                        // 0x0020 (SWP_FRAMECHANGED) -> Required after style change!
                        SetWindowPos(handle, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0040 | 0x0020);
                        
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Could not position OpenCPN: {ex.Message}");
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