using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace USV_Controller_V1._1
{
    public partial class CameraWindow : Window
    {
        private const string CAMERA_IP = "192.168.80.68";
        private const int CAMERA_PORT = 2020;
        private const string CAMERA_USER = "admin";
        private const string CAMERA_PASSWORD = "SMBSMBSMB12";
        private const int CAMERA_CHANNEL = 1;

        private int userId = -1;
        private int realPlayHandle = -1;
        private WinForms.PictureBox videoPanel;

        private bool isPTZMoving = false;
        private uint currentPTZCommand = 0;
        private uint ptzSpeed = 4;

        private readonly HikvisionPtzController? _ptzController;

        // ✅ Ping loop control
        private CancellationTokenSource _pingCts = new CancellationTokenSource();
        private bool _sdkConnected = false;

        public CameraWindow(HikvisionPtzController? ptzController = null)
        {
            InitializeComponent();

            _ptzController = ptzController;

            videoPanel = new WinForms.PictureBox
            {
                Dock = WinForms.DockStyle.Fill,
                BackColor = System.Drawing.Color.Black
            };
            videoHost.Child = videoPanel;

            this.Loaded += Window_Loaded;
            this.Closing += Window_Closing;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // ✅ Just show waiting UI — no SDK calls at all
            UpdateStatus("📡 Waiting for camera...", Colors.Gray);
            UpdateCameraStatus("⚫ Offline", Colors.Gray);

            // ✅ Start background ping loop immediately
            StartPingLoop(_pingCts.Token);
        }

        // ✅ Runs forever in background — connects SDK once camera is reachable
        private void StartPingLoop(CancellationToken token)
        {
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_sdkConnected)
                        {
                            bool reachable = await PingCamera();

                            if (reachable)
                            {
                                Dispatcher.Invoke(() =>
                                    UpdateStatus("📡 Camera found — connecting...", Colors.Orange));

                                // ✅ Connect SDK on background thread
                                bool connected = await Task.Run(() => ConnectSdk());

                                if (connected)
                                {
                                    _sdkConnected = true;
                                    Dispatcher.Invoke(() =>
                                    {
                                        UpdateStatus("✅ Camera connected!", Colors.LimeGreen);
                                        LoadingOverlay.Visibility = Visibility.Collapsed;
                                    });

                                    // Start video stream
                                    Dispatcher.Invoke(() => StartRealPlay());
                                }
                                else
                                {
                                    Dispatcher.Invoke(() =>
                                        UpdateStatus("⚠️ SDK connect failed — retrying...", Colors.Orange));
                                }
                            }
                            else
                            {
                                Dispatcher.Invoke(() =>
                                    UpdateStatus($"📡 Waiting for camera at {CAMERA_IP}...", Colors.Gray));
                            }
                        }
                        else
                        {
                            // ✅ Already connected — just do a health ping every 5s
                            bool alive = await PingCamera();
                            if (!alive)
                            {
                                _sdkConnected = false;
                                Dispatcher.Invoke(() =>
                                {
                                    UpdateStatus("⚠️ Camera lost — reconnecting...", Colors.Orange);
                                    UpdateCameraStatus("⚫ Offline", Colors.Gray);
                                    LoadingOverlay.Visibility = Visibility.Visible;
                                });
                                DisconnectSdk();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ping loop error: {ex.Message}");
                    }

                    // ✅ Ping every 1 second when offline, 5 seconds when online
                    await Task.Delay(_sdkConnected ? 5000 : 3000, token)
                              .ContinueWith(_ => { }); // suppress cancellation exception
                }
            }, token);
        }

        private async Task<bool> PingCamera()
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(CAMERA_IP, 1500);
                return reply.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        // ✅ All SDK calls isolated here — returns true/false, never throws
        private bool ConnectSdk()
        {
            try
            {
                bool initResult = CHCNetSDK.NET_DVR_Init();
                if (!initResult) return false;

                CHCNetSDK.NET_DVR_SetConnectTime(2000u, 1u);
                CHCNetSDK.NET_DVR_SetReconnect(10000u, true);

                // Try V30 login
                CHCNetSDK.NET_DVR_DEVICEINFO_V30 deviceInfoV30 = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
                deviceInfoV30.sSerialNumber = new byte[48];
                deviceInfoV30.byRes2 = new byte[9];

                userId = CHCNetSDK.NET_DVR_Login_V30(
                    CAMERA_IP, CAMERA_PORT, CAMERA_USER, CAMERA_PASSWORD, ref deviceInfoV30);

                // Fallback V40
                if (userId < 0)
                {
                    CHCNetSDK.NET_DVR_USER_LOGIN_INFO loginInfo = new CHCNetSDK.NET_DVR_USER_LOGIN_INFO();
                    loginInfo.Init();
                    loginInfo.sDeviceAddress = CAMERA_IP;
                    loginInfo.wPort = (ushort)CAMERA_PORT;
                    loginInfo.sUserName = CAMERA_USER;
                    loginInfo.sPassword = CAMERA_PASSWORD;
                    loginInfo.byUseTransport = 0;

                    CHCNetSDK.NET_DVR_DEVICEINFO_V40 deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V40();
                    deviceInfo.Init();

                    userId = CHCNetSDK.NET_DVR_Login_V40(ref loginInfo, ref deviceInfo);
                }

                if (userId < 0)
                {
                    uint errorCode = CHCNetSDK.NET_DVR_GetLastError();
                    Dispatcher.Invoke(() =>
                        UpdateStatus($"❌ Login failed! Error {errorCode}", Colors.Red));
                    return false;
                }

                Dispatcher.Invoke(() => UpdateCameraStatus("🟢 Connected", Colors.LimeGreen));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SDK connect error: {ex.Message}");
                return false;
            }
        }

        private void StartRealPlay()
        {
            try
            {
                if (userId < 0) return;

                UpdateStatus("▶️ Starting video stream...", Colors.Orange);

                if (!videoPanel.IsHandleCreated)
                {
                    var _ = videoPanel.Handle;
                }

                Thread.Sleep(300);

                CHCNetSDK.NET_DVR_PREVIEWINFO previewInfo = new CHCNetSDK.NET_DVR_PREVIEWINFO();
                previewInfo.Init();
                previewInfo.lChannel = CAMERA_CHANNEL;
                previewInfo.dwStreamType = 0;
                previewInfo.dwLinkMode = 0;
                previewInfo.hPlayWnd = videoPanel.Handle;
                previewInfo.bBlocked = 1;
                previewInfo.bPassbackRecord = 0;
                previewInfo.byPreviewMode = 0;
                previewInfo.byProtoType = 0;
                previewInfo.byRes1 = 0;
                previewInfo.byVideoCodingType = 0;
                previewInfo.dwDisplayBufNum = 1;
                previewInfo.byNPQMode = 0;

                realPlayHandle = CHCNetSDK.NET_DVR_RealPlay_V40(
                    userId, ref previewInfo, null, IntPtr.Zero);

                if (realPlayHandle < 0)
                {
                    realPlayHandle = CHCNetSDK.NET_DVR_RealPlay_V30(
                        userId, ref previewInfo, null, IntPtr.Zero, 1u);
                }

                if (realPlayHandle < 0)
                {
                    uint errorCode = CHCNetSDK.NET_DVR_GetLastError();
                    UpdateStatus($"❌ Stream failed! Error {errorCode}", Colors.Red);
                    return;
                }

                LoadingOverlay.Visibility = Visibility.Collapsed;
                UpdateStatus("🎥 Live stream active", Colors.LimeGreen);
                UpdateCameraStatus("🔴 Live", Colors.Red);
            }
            catch (Exception ex)
            {
                UpdateStatus($"❌ Preview error: {ex.Message}", Colors.Red);
            }
        }

        // ✅ Clean SDK disconnect without closing the window
        private void DisconnectSdk()
        {
            try
            {
                if (isPTZMoving && currentPTZCommand != 0)
                {
                    CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(
                        userId, CAMERA_CHANNEL, currentPTZCommand, 1u, 0u);
                    isPTZMoving = false;
                    currentPTZCommand = 0;
                }

                if (realPlayHandle >= 0)
                {
                    CHCNetSDK.NET_DVR_StopRealPlay(realPlayHandle);
                    realPlayHandle = -1;
                }

                if (userId >= 0)
                {
                    CHCNetSDK.NET_DVR_Logout(userId);
                    userId = -1;
                }
            }
            catch { }
        }

        public void ControlPTZ(int povPosition)
        {
            if (_ptzController != null && _ptzController.IsConnected)
            {
                ControlPTZViaController(povPosition);
                return;
            }

            if (userId < 0 || realPlayHandle < 0) return;
            ControlPTZDirectSdk(povPosition);
        }

        private void ControlPTZViaController(int povPosition)
        {
            try
            {
                bool shouldMove = GetPtzCommandFromPov(povPosition, out uint command);
                if (shouldMove)
                    _ptzController!.PTZMove(command, ptzSpeed);
                else
                    _ptzController!.PTZStop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PTZ controller error: {ex.Message}");
            }
        }

        private void ControlPTZDirectSdk(int povPosition)
        {
            try
            {
                bool shouldMove = GetPtzCommandFromPov(povPosition, out uint command);

                if (shouldMove)
                {
                    if (!isPTZMoving || currentPTZCommand != command)
                    {
                        if (isPTZMoving && currentPTZCommand != command)
                        {
                            CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(
                                userId, CAMERA_CHANNEL, currentPTZCommand, 1u, 0u);
                        }
                        CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(
                            userId, CAMERA_CHANNEL, command, 0u, ptzSpeed);
                        isPTZMoving = true;
                        currentPTZCommand = command;
                    }
                }
                else if (isPTZMoving)
                {
                    CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(
                        userId, CAMERA_CHANNEL, currentPTZCommand, 1u, 0u);
                    isPTZMoving = false;
                    currentPTZCommand = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PTZ SDK error: {ex.Message}");
            }
        }

        private bool GetPtzCommandFromPov(int povPosition, out uint command)
        {
            command = 0;
            if (povPosition == -1) return false;

            int angle = povPosition;

            if (angle == 0 || angle >= 33750 || angle <= 2250)
                command = CHCNetSDK.TILT_UP;
            else if (angle > 2250 && angle < 6750)
                command = CHCNetSDK.UP_RIGHT;
            else if (angle >= 6750 && angle <= 11250)
                command = CHCNetSDK.PAN_RIGHT;
            else if (angle > 11250 && angle < 15750)
                command = CHCNetSDK.DOWN_RIGHT;
            else if (angle >= 15750 && angle <= 20250)
                command = CHCNetSDK.TILT_DOWN;
            else if (angle > 20250 && angle < 24750)
                command = CHCNetSDK.DOWN_LEFT;
            else if (angle >= 24750 && angle <= 29250)
                command = CHCNetSDK.PAN_LEFT;
            else
                command = CHCNetSDK.UP_LEFT;

            return true;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // ✅ Stop ping loop first
            _pingCts.Cancel();

            DisconnectSdk();

            try { CHCNetSDK.NET_DVR_Cleanup(); } catch { }
        }

        private void UpdateStatus(string message, Color color)
        {
            Dispatcher.Invoke(() =>
            {
                txtStatus.Text = message;
                txtStatus.Foreground = new SolidColorBrush(color);
            });
        }

        private void UpdateCameraStatus(string status, Color color)
        {
            Dispatcher.Invoke(() =>
            {
                CameraStatus.Text = status;
                StreamingIndicator.Fill = new SolidColorBrush(color);
            });
        }
    }
}