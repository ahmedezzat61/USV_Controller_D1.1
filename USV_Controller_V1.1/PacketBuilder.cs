using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SharpDX.XInput;
using SkiaSharp;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace USV_Controller_V1._1
{
    // Partial class - contains packet construction helpers (moved from MainWindow)
    public partial class MainWindow
    {
        private int ScaleAxis(short value)
        {
            // Map [-32768, 32767] → [0, 1023]
            return (int)((32768 - value) / 64.0);
        }

        // ✅ UNIFIED METHOD - Works for both USV steering and camera control
        // Added optional deadzone parameter (default 0 for camera, can specify for steering)
        private sbyte ScaleThumbToSigned(short value, short deadzone = 0)
        {
            // Apply deadzone if specified
            if (Math.Abs(value) < deadzone) return 0;

            // Map [-32768, 32767] -> [-127, 127] (sbyte range)
            // Use 32767 to avoid overflow; clamp to -127..127
            double normalized = value >= 0 ? value / 32767.0 : value / 32768.0;
            int scaled = (int)Math.Round(normalized * 127.0);
            scaled = Math.Clamp(scaled, -127, 127);
            return (sbyte)scaled;
        }

        // ✅ Camera packet builder - uses RightThumb for Pan/Tilt
        private byte[] BuildCameraPacket()
        {
            // RightThumbX controls pan (left/right)
            sbyte camPan = ScaleThumbToSigned(_gamepad.RightThumbX);

            // RightThumbY controls tilt (up/down)
            sbyte camTilt = ScaleThumbToSigned(_gamepad.RightThumbY);

            byte camFlags = 0x00;   // Reserved for future use
            byte camResvd = 0x00;   // Reserved

            return new byte[]
            {
                unchecked((byte)camPan),
                unchecked((byte)camTilt),
                camFlags,
                camResvd
            };
        }

        // ✅ USV packet builder (14 bytes) - unchanged
        private byte[] BuildPacket()
        {
            int yAxis = ScaleAxis(_gamepad.LeftThumbY);
            yAxis = Math.Clamp(yAxis, 0, 1023);

            int zAxis = 512 + (int)((_gamepad.RightTrigger - _gamepad.LeftTrigger) / 255.0 * 512);
            zAxis = Math.Clamp(zAxis, 0, 1023);

            byte a = (_gamepad.Buttons & GamepadButtonFlags.A) != 0 ? (byte)1 : (byte)0;
            byte b = (_gamepad.Buttons & GamepadButtonFlags.B) != 0 ? (byte)1 : (byte)0;
            byte x = (_gamepad.Buttons & GamepadButtonFlags.X) != 0 ? (byte)1 : (byte)0;
            byte y = (_gamepad.Buttons & GamepadButtonFlags.Y) != 0 ? (byte)1 : (byte)0;
            byte lb = (_gamepad.Buttons & GamepadButtonFlags.LeftShoulder) != 0 ? (byte)1 : (byte)0;
            byte rb = (_gamepad.Buttons & GamepadButtonFlags.RightShoulder) != 0 ? (byte)1 : (byte)0;
            byte up = (_gamepad.Buttons & GamepadButtonFlags.DPadUp) != 0 ? (byte)1 : (byte)0;
            byte down = (_gamepad.Buttons & GamepadButtonFlags.DPadDown) != 0 ? (byte)1 : (byte)0;
            byte left = (_gamepad.Buttons & GamepadButtonFlags.DPadLeft) != 0 ? (byte)1 : (byte)0;
            byte right = (_gamepad.Buttons & GamepadButtonFlags.DPadRight) != 0 ? (byte)1 : (byte)0;

            byte[] packet = new byte[14];
            packet[0] = (byte)(yAxis >> 8);
            packet[1] = (byte)(yAxis & 0xFF);
            packet[2] = (byte)(zAxis >> 8);
            packet[3] = (byte)(zAxis & 0xFF);
            packet[4] = a;
            packet[5] = b;
            packet[6] = x;
            packet[7] = y;
            packet[8] = lb;
            packet[9] = rb;
            packet[10] = down;
            packet[11] = up;
            packet[12] = left;
            packet[13] = right;

            return packet;
        }
    }
}