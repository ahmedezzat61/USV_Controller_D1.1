using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SharpDX.DirectInput;
using SkiaSharp;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace USV_Controller_V1._1
{
    public partial class MainWindow
    {
        // ✅ Packet sequence counter
        private byte _usvSequence = 0;
        private byte _espSequence = 0;

        // ✅ Scale DirectInput axis (0-65535) to your required range (0-1023)
        private int ScaleAxis(int value)
        {
            return (int)((value / 65535.0) * 1023.0);
        }

        // ✅ Scale DirectInput axis REVERSED (for throttle)
        private int ScaleAxisReversed(int value)
        {
            return 1023 - (int)((value / 65535.0) * 1023.0);
        }

        // ✅ Scale centered axis (like rudder) to 0-1023 range
        private int ScaleCenteredAxis(int value)
        {
            int centered = value - 32768;
            return (int)((centered + 32768) / 64.0);
        }

        // ✅ Scale centered axis to signed byte for camera control
        private sbyte ScaleAxisToSignedByte(int value, int deadzone = 3000)
        {
            int centered = value - 32768;

            if (Math.Abs(centered) < deadzone) return 0;

            double normalized = centered >= 0 ? centered / 32767.0 : centered / 32768.0;
            int scaled = (int)Math.Round(normalized * 127.0);
            scaled = Math.Clamp(scaled, -127, 127);
            return (sbyte)scaled;
        }

        private double ScaleSliderToZoom(int value, int deadzone = 5000)
        {
            int centered = value - 32768;

            if (Math.Abs(centered) < deadzone) return 0;

            double normalized = centered >= 0 ? centered / 32767.0 : centered / 32768.0;

            return -Math.Clamp(normalized, -1.0, 1.0);
        }

        // ✅ Calculate XOR checksum
        private byte CalculateChecksum(byte[] data, int length)
        {
            byte checksum = 0;
            for (int i = 0; i < length; i++)
            {
                checksum ^= data[i];
            }
            return checksum;
        }

        // ✅ BUILD ARDUINO/USV PACKET (23 bytes total)
        // Structure: [0xAA] [0x55] [SEQ] [14 data bytes] [CHECKSUM]
        private byte[] BuildPacket()
        {
            // Safety check for disconnected joystick
            if (_joystickState == null)
            {
                byte[] safePacket = new byte[23];
                safePacket[0] = 0xAA;  // Start byte 1
                safePacket[1] = 0x55;  // Start byte 2
                safePacket[2] = _usvSequence++;  // Sequence

                // Neutral data: speedAxis = 1023 (zero throttle), steeringAxis = 512 (center)
                safePacket[3] = 0x03;  // Speed high byte (1023)
                safePacket[4] = 0xFF;  // Speed low byte
                safePacket[5] = 0x02;  // Steering high byte (512)
                safePacket[6] = 0x00;  // Steering low byte

                // All other bytes remain 0
                for (int i = 7; i < 17; i++)
                {
                    safePacket[i] = 0;
                }

                // Calculate checksum
                safePacket[17] = CalculateChecksum(safePacket, 17);

                return safePacket;
            }

            int throttleValue = _joystickState.Z;

            // Safety check: Operator must move throttle to minimum first
            if (!_throttleZeroed)
            {
                if (throttleValue > 60000)
                {
                    _throttleZeroed = true;
                    Dispatcher.Invoke(() => Log("✅ Throttle zeroed - control enabled"));
                }
                else
                {
                    throttleValue = 65535;
                }
            }

            // Get control values
            int speedAxis = ScaleAxisReversed(throttleValue);
            speedAxis = Math.Clamp(speedAxis, 0, 1023);

            int steeringAxis = ScaleCenteredAxis(_joystickState.RotationZ);
            steeringAxis = Math.Clamp(steeringAxis, 0, 1023);

            // Button states
            byte start = _joystickState.Buttons[7] ? (byte)1 : (byte)0;
            byte march = _joystickState.Buttons[8] ? (byte)1 : (byte)0;
            byte b = _joystickState.Buttons[9] ? (byte)1 : (byte)0;
            byte tiltup = _joystickState.Buttons[10] ? (byte)1 : (byte)0;
            byte tiltdown = _joystickState.Buttons[11] ? (byte)1 : (byte)0;
            byte rb = _joystickState.Buttons[12] ? (byte)1 : (byte)0;
            byte forward = _joystickState.Buttons[15] ? (byte)1 : (byte)0;
            byte backward = _joystickState.Buttons[13] ? (byte)1 : (byte)0;
            byte nutral = _joystickState.Buttons[14] ? (byte)1 : (byte)0;
            byte watchdog = 0;

            // Build packet: [0xAA] [0x55] [SEQ] [14 data bytes] [CHECKSUM]
            byte[] packet = new byte[18];

            // Header
            packet[0] = 0xAA;  // Start byte 1
            packet[1] = 0x55;  // Start byte 2
            packet[2] = _usvSequence++;  // Sequence number

            // Data payload (14 bytes)
            packet[3] = (byte)(speedAxis >> 8);      // Speed high byte
            packet[4] = (byte)(speedAxis & 0xFF);    // Speed low byte
            packet[5] = (byte)(steeringAxis >> 8);   // Steering high byte
            packet[6] = (byte)(steeringAxis & 0xFF); // Steering low byte
            packet[7] = start;
            packet[8] = b;
            packet[9] = tiltup;
            packet[10] = tiltdown;
            packet[11] = march;
            packet[12] = rb;
            packet[13] = backward;
            packet[14] = forward;
            packet[15] = nutral;
            packet[16] = watchdog;

            // Checksum (XOR of all previous bytes)
            packet[17] = CalculateChecksum(packet, 17);

            return packet;
        }

        // ✅ BUILD ESP32 PACKET (8 bytes total)
        // Structure: [0xAA] [0x55] [SEQ] [4 data bytes] [CHECKSUM]
        private byte[] BuildPacket2()
        {
            if (_joystickState == null)
            {
                byte[] safePacket = new byte[8];
                safePacket[0] = 0xAA;
                safePacket[1] = 0x55;
                safePacket[2] = _espSequence++;
                safePacket[3] = 0;  // watchdog
                safePacket[4] = 0;  // explode
                safePacket[5] = 0;  // position relay
                safePacket[6] = 0;  // speed relay
                safePacket[7] = CalculateChecksum(safePacket, 7);
                return safePacket;
            }

            byte watchdog = _joystickState.Buttons[12] ? (byte)1 : (byte)0;

            bool isExplode1Pressed = _joystickState.Buttons[0];
            bool isExplode2Pressed = _joystickState.Buttons[5];
            bool shouldExplode = isExplode1Pressed && isExplode2Pressed;
            byte explodeFinal = shouldExplode ? (byte)1 : (byte)0;

            // Build packet: [0xAA] [0x55] [SEQ] [4 data bytes] [CHECKSUM]
            byte[] packet = new byte[8];

            // Header
            packet[0] = 0xAA;  // Start byte 1
            packet[1] = 0x55;  // Start byte 2
            packet[2] = _espSequence++;  // Sequence number

            // Data payload (4 bytes)
            packet[3] = watchdog;
            packet[4] = explodeFinal;
            packet[5] = _positionRelayFeedback;
            packet[6] = _speedRelayFeedback;

            // Checksum
            packet[7] = CalculateChecksum(packet, 7);

            return packet;
        }

        // ✅ VALIDATE AND PARSE ARDUINO FEEDBACK (13 bytes total)
        // Structure: [0xAA] [0x55] [SEQ] [9 data bytes] [CHECKSUM]
        // Data: [Feedback1_H] [Feedback1_L] [Feedback2_H] [Feedback2_L] [Feedback3_H] [Feedback3_L] [Relay1] [PositionRelay] [SpeedRelay]
        private bool ParseArduinoFeedback(byte[] data, out ArduinoFeedback feedback)
        {
            feedback = new ArduinoFeedback();

            // Validate packet length
            if (data == null || data.Length < 13)
                return false;

            // Validate start bytes
            if (data[0] != 0xAA || data[1] != 0x55)
                return false;

            // Validate checksum
            byte receivedChecksum = data[12];
            byte calculatedChecksum = CalculateChecksum(data, 12);
            if (receivedChecksum != calculatedChecksum)
                return false;

            // Extract data
            feedback.Sequence = data[2];
            feedback.Feedback1 = (data[3] << 8) | data[4];  // 2 bytes
            feedback.Feedback2 = (data[5] << 8) | data[6];  // 2 bytes
            feedback.Feedback3 = (data[7] << 8) | data[8];  // 2 bytes
            feedback.Relay1 = data[9] == 1;
            feedback.PositionRelay = data[10];  // ✅ This gets forwarded to ESP
            feedback.SpeedRelay = data[11];     // ✅ This gets forwarded to ESP

            return true;
        }

        // ✅ VALIDATE AND PARSE ESP32 FEEDBACK (10 bytes total)
        // Structure: [0xAA] [0x55] [SEQ] [5 data bytes] [CHECKSUM]
        private bool ParseEspFeedback(byte[] data, out EspFeedback feedback)
        {
            feedback = new EspFeedback();

            // Validate packet length
            if (data == null || data.Length < 9)
                return false;

            // Validate start bytes
            if (data[0] != 0xAA || data[1] != 0x55)
                return false;

            // Validate checksum
            byte receivedChecksum = data[8];
            byte calculatedChecksum = CalculateChecksum(data, 8);
            if (receivedChecksum != calculatedChecksum)
                return false;

            // Extract data
            feedback.Sequence = data[2];
            feedback.Relay1 = data[3] == 1;  // Door/Gate
            feedback.Relay2 = data[4] == 1;  // Position
            feedback.Relay3 = data[5] == 1;  // Speed
            feedback.Relay4 = data[6] == 1;  // Fire
            feedback.Relay5 = data[7] == 1;  // Watchdog

            return true;
        }
    }

    // ✅ Feedback data structures
    public struct ArduinoFeedback
    {
        public byte Sequence;
        public int Feedback1;
        public int Feedback2;
        public int Feedback3;
        public bool Relay1;
        public byte PositionRelay;
        public byte SpeedRelay;
    }

    public struct EspFeedback
    {
        public byte Sequence;
        public bool Relay1;  // Door/Gate (RFID)
        public bool Relay2;  // Position
        public bool Relay3;  // Speed
        public bool Relay4;  // Fire
        public bool Relay5;  // Watchdog
    }
}