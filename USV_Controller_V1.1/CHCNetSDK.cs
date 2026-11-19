using System;
using System.Runtime.InteropServices;

namespace USV_Controller_V1._1
{
    public class CHCNetSDK
    {
        // DLL name
        public const string NETSDK_DLL = "HCNetSDK.dll";

        // PTZ Commands
        public const uint LIGHT_PWRON = 2;
        public const uint WIPER_PWRON = 3;
        public const uint FAN_PWRON = 4;
        public const uint HEATER_PWRON = 5;
        public const uint AUX_PWRON1 = 6;
        public const uint AUX_PWRON2 = 7;
        public const uint ZOOM_IN = 11;
        public const uint ZOOM_OUT = 12;
        public const uint FOCUS_NEAR = 13;
        public const uint FOCUS_FAR = 14;
        public const uint IRIS_OPEN = 15;
        public const uint IRIS_CLOSE = 16;
        public const uint TILT_UP = 21;
        public const uint TILT_DOWN = 22;
        public const uint PAN_LEFT = 23;
        public const uint PAN_RIGHT = 24;
        public const uint UP_LEFT = 25;
        public const uint UP_RIGHT = 26;
        public const uint DOWN_LEFT = 27;
        public const uint DOWN_RIGHT = 28;
        public const uint PAN_AUTO = 29;

        // Structures
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct NET_DVR_DEVICEINFO_V30
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] sSerialNumber;
            public byte byAlarmInPortNum;
            public byte byAlarmOutPortNum;
            public byte byDiskNum;
            public byte byDVRType;
            public byte byChanNum;
            public byte byStartChan;
            public byte byAudioChanNum;
            public byte byIPChanNum;
            public byte byZeroChanNum;
            public byte byMainProto;
            public byte bySubProto;
            public byte bySupport;
            public byte bySupport1;
            public byte bySupport2;
            public ushort wDevType;
            public byte bySupport3;
            public byte byMultiStreamProto;
            public byte byStartDChan;
            public byte byStartDTalkChan;
            public byte byHighDChanNum;
            public byte bySupport4;
            public byte byLanguageType;
            public byte byVoiceInChanNum;
            public byte byStartVoiceInChanNo;
            public byte bySupport5;
            public byte bySupport6;
            public byte byMirrorChanNum;
            public ushort wStartMirrorChanNo;
            public byte bySupport7;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
            public byte[] byRes2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NET_DVR_DEVICEINFO_V40
        {
            public NET_DVR_DEVICEINFO_V30 struDeviceV30;
            public byte bySupportLock;
            public byte byRetryLoginTime;
            public byte byPasswordLevel;
            public byte byProxyType;
            public uint dwSurplusLockTime;
            public byte byCharEncType;
            public byte bySupportDev5;
            public byte bySupport;
            public byte byLoginMode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 253)]
            public byte[] byRes2;

            public void Init()
            {
                struDeviceV30.sSerialNumber = new byte[48];
                struDeviceV30.byRes2 = new byte[9];
                byRes2 = new byte[253];
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct NET_DVR_USER_LOGIN_INFO
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string sDeviceAddress;
            public byte byUseTransport;
            public ushort wPort;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string sUserName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string sPassword;

            public void Init()
            {
                sDeviceAddress = "";
                sUserName = "";
                sPassword = "";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NET_DVR_PREVIEWINFO
        {
            public int lChannel;
            public uint dwStreamType;
            public uint dwLinkMode;
            public IntPtr hPlayWnd;
            public uint bBlocked;
            public uint bPassbackRecord;
            public byte byPreviewMode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public byte[] byStreamID;
            public byte byProtoType;
            public byte byRes1;
            public byte byVideoCodingType;
            public uint dwDisplayBufNum;
            public byte byNPQMode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 215)]
            public byte[] byRes;

            public void Init()
            {
                byStreamID = new byte[3];
                byRes = new byte[215];
            }
        }

        // Delegate for callbacks
        public delegate void REALDATACALLBACK(int lRealHandle, uint dwDataType, IntPtr pBuffer, uint dwBufSize, IntPtr pUser);

        // SDK Functions
        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_Init();

        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_Cleanup();

        [DllImport(NETSDK_DLL)]
        public static extern int NET_DVR_Login_V30(string sDVRIP, int wDVRPort, string sUserName, string sPassword, ref NET_DVR_DEVICEINFO_V30 lpDeviceInfo);

        [DllImport(NETSDK_DLL)]
        public static extern int NET_DVR_Login_V40(ref NET_DVR_USER_LOGIN_INFO pLoginInfo, ref NET_DVR_DEVICEINFO_V40 lpDeviceInfo);

        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_Logout(int iUserID);

        [DllImport(NETSDK_DLL)]
        public static extern uint NET_DVR_GetLastError();

        [DllImport(NETSDK_DLL)]
        public static extern int NET_DVR_RealPlay_V40(int iUserID, ref NET_DVR_PREVIEWINFO lpPreviewInfo, REALDATACALLBACK fRealDataCallBack_V30, IntPtr pUser);

        [DllImport(NETSDK_DLL)]
        public static extern int NET_DVR_RealPlay_V30(int iUserID, ref NET_DVR_PREVIEWINFO lpPreviewInfo, REALDATACALLBACK fRealDataCallBack_V30, IntPtr pUser, uint bBlocked);

        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_StopRealPlay(int iRealHandle);

        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_PTZControlWithSpeed_Other(int lUserID, int lChannel, uint dwPTZCommand, uint dwStop, uint dwSpeed);

        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_PTZControl_Other(int lUserID, int lChannel, uint dwPTZCommand, uint dwStop);

        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_SetConnectTime(uint dwWaitTime, uint dwTryTimes);

        [DllImport(NETSDK_DLL)]
        public static extern bool NET_DVR_SetReconnect(uint dwInterval, bool bEnableRecon);
    }
}