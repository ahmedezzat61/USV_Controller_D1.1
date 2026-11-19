using System;
using System.Threading.Tasks;

namespace USV_Controller_V1._1
{
    public class HikvisionPtzController : IDisposable
    {
        private readonly string _cameraIp;
        private readonly int _cameraPort;
        private readonly string _username;
        private readonly string _password;
        
        public int UserId { get; private set; } = -1;
        public int RealPlayHandle { get; private set; } = -1;
        public bool IsConnected => UserId >= 0;

        private bool _isPtzMoving = false;
        private uint _currentPtzCommand = 0;

        public event Action<string, System.Windows.Media.Color>? OnStatusUpdate;

        public HikvisionPtzController(string cameraIp, int cameraPort, string username, string password)
        {
            _cameraIp = cameraIp;
            _cameraPort = cameraPort;
            _username = username;
            _password = password;
        }

        public bool Initialize()
        {
            try
            {
                bool initResult = CHCNetSDK.NET_DVR_Init();
                if (!initResult)
                {
                    LogStatus("SDK init failed", System.Windows.Media.Colors.Red);
                    return false;  // ✅ Return false, never throw
                }

                CHCNetSDK.NET_DVR_SetConnectTime(2000u, 1u);
                CHCNetSDK.NET_DVR_SetReconnect(10000u, true);

                LogStatus($"Connecting to {_cameraIp}...", System.Windows.Media.Colors.Orange);

                // V30 login attempt
                CHCNetSDK.NET_DVR_DEVICEINFO_V30 deviceInfoV30 = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
                deviceInfoV30.sSerialNumber = new byte[48];
                deviceInfoV30.byRes2 = new byte[9];

                UserId = CHCNetSDK.NET_DVR_Login_V30(
                    _cameraIp, _cameraPort, _username, _password, ref deviceInfoV30);

                // V40 fallback
                if (UserId < 0)
                {
                    CHCNetSDK.NET_DVR_USER_LOGIN_INFO loginInfo = new CHCNetSDK.NET_DVR_USER_LOGIN_INFO();
                    loginInfo.Init();
                    loginInfo.sDeviceAddress = _cameraIp;
                    loginInfo.wPort = (ushort)_cameraPort;
                    loginInfo.sUserName = _username;
                    loginInfo.sPassword = _password;
                    loginInfo.byUseTransport = 0;

                    CHCNetSDK.NET_DVR_DEVICEINFO_V40 deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V40();
                    deviceInfo.Init();

                    UserId = CHCNetSDK.NET_DVR_Login_V40(ref loginInfo, ref deviceInfo);
                }

                if (UserId < 0)
                {
                    uint errorCode = CHCNetSDK.NET_DVR_GetLastError();
                    LogStatus($"Login failed! Error {errorCode} — PTZ disabled", System.Windows.Media.Colors.Orange);
                    return false;  // ✅ Graceful failure, app continues
                }

                LogStatus("✅ Camera Connected!", System.Windows.Media.Colors.LimeGreen);
                return true;
            }
            catch (Exception ex)
            {
                // ✅ Catch ALL exceptions including SEH from native SDK
                LogStatus($"PTZ init failed: {ex.Message}", System.Windows.Media.Colors.Orange);
                UserId = -1;
                return false;
            }
        }

        public bool StartRealPlay(IntPtr windowHandle)
        {
            if (UserId < 0) return false;
            
            try
            {
                CHCNetSDK.NET_DVR_PREVIEWINFO previewInfo = new CHCNetSDK.NET_DVR_PREVIEWINFO();
                previewInfo.Init();
                
                previewInfo.lChannel = 1;
                previewInfo.dwStreamType = 0; // Main stream
                previewInfo.dwLinkMode = 0; // TCP
                previewInfo.hPlayWnd = windowHandle;
                previewInfo.bBlocked = 1;

                RealPlayHandle = CHCNetSDK.NET_DVR_RealPlay_V40(UserId, ref previewInfo, null, IntPtr.Zero);

                if (RealPlayHandle < 0)
                {
                    RealPlayHandle = CHCNetSDK.NET_DVR_RealPlay_V30(UserId, ref previewInfo, null, IntPtr.Zero, 1);
                }

                if (RealPlayHandle < 0)
                {
                    uint errorCode = CHCNetSDK.NET_DVR_GetLastError();
                    LogStatus($"Stream failed! Error {errorCode}", System.Windows.Media.Colors.Red);
                    return false;
                }

                LogStatus("🎥 Live stream active", System.Windows.Media.Colors.LimeGreen);
                return true;
            }
            catch (Exception ex)
            {
                LogStatus($"Preview error: {ex.Message}", System.Windows.Media.Colors.Red);
                return false;
            }
        }

        public void StopRealPlay()
        {
            if (RealPlayHandle >= 0)
            {
                CHCNetSDK.NET_DVR_StopRealPlay(RealPlayHandle);
                RealPlayHandle = -1;
            }
        }

        public void PTZMove(uint command, uint speed = 4)
        {
            if (UserId < 0) return;

            // Constrain speed to 1-7 (Hikvision valid range)
            speed = Math.Max(1, Math.Min(7, speed));

            if (!_isPtzMoving || _currentPtzCommand != command)
            {
                if (_isPtzMoving && _currentPtzCommand != command)
                {
                    // Stop previous command first
                    CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(UserId, 1, _currentPtzCommand, 1u, 0u);
                }
                
                CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(UserId, 1, command, 0u, speed);
                _isPtzMoving = true;
                _currentPtzCommand = command;
            }
        }

        public void PTZStop()
        {
            if (UserId < 0 || !_isPtzMoving) return;

            CHCNetSDK.NET_DVR_PTZControlWithSpeed_Other(UserId, 1, _currentPtzCommand, 1u, 0u);
            _isPtzMoving = false;
            _currentPtzCommand = 0;
        }

        private void LogStatus(string message, System.Windows.Media.Color color)
        {
            OnStatusUpdate?.Invoke(message, color);
            System.Diagnostics.Debug.WriteLine($"Hikvision Controller: {message}");
        }

        public void Dispose()
        {
            StopRealPlay();

            if (UserId >= 0)
            {
                CHCNetSDK.NET_DVR_Logout(UserId);
                UserId = -1;
            }

            CHCNetSDK.NET_DVR_Cleanup();
        }
    }
}
