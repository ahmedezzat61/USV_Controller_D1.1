using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SharpDX.DirectInput;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vlc.DotNet.Core.Interops.Signatures;
using System.Windows.Input;

namespace USV_Controller_V1._1
{
    public partial class MainWindow
    {
        // ✅ Packet sequence counter
        private byte _usvSequence = 0;
        private byte _espSequence = 0;
        private int _lastSystemFaultState = 0;
        private byte _watchdogEsp = 0;

        // ✅ Button Press Timers
        private DateTime _watchdogPressStartTime = DateTime.MinValue;
        private DateTime _explodePressStartTime = DateTime.MinValue;
        private DateTime _button2PressStartTime = DateTime.MinValue;  // ✅ ADD THIS


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

        private byte watchdogEsp(byte value)
        {
            // If joystick is not available, restore to 0
            if (_joystickState == null)
            {
                _button2PressStartTime = DateTime.MinValue;
                return 0;  // ✅ Auto-restore when joystick lost
            }

            // Check if button 10 is pressed
            if (_joystickState.Buttons[9])
            {
                // Start timing if this is first press
                if (_button2PressStartTime == DateTime.MinValue)
                {
                    _button2PressStartTime = DateTime.UtcNow;
                    Dispatcher.Invoke(() => Log("⏱️ Button 2 pressed - hold for 3 seconds to reset Explosive Circuit..."));
                }

                // Check if held for 3 seconds
                if ((DateTime.UtcNow - _button2PressStartTime).TotalSeconds >= 3)
                {
                    // Reset watchdog ONCE
                    if (value == 1)  // ✅ Only log once
                    {
                        Dispatcher.Invoke(() => Log("✅ Button 2 held for 3s - Watchdog ESP resets"));
                    }
                    return 1;  // Stay at 1 while button is held
                }
            }
            else
            {
                // Button released
                if (_button2PressStartTime != DateTime.MinValue)
                {
                    // If it was being pressed but not for 3 seconds
                    if ((DateTime.UtcNow - _button2PressStartTime).TotalSeconds < 3)
                    {
                        Dispatcher.Invoke(() => Log("⚠️ Button 2 released early - watchdog not reset"));
                    }
                    else if (value == 0)
                    {
                        // Was at 0, now restoring
                        Dispatcher.Invoke(() => Log("✅ Button 2 released - Resting the explosive circuit "));
                    }
                }
                _button2PressStartTime = DateTime.MinValue;
                return 0;  // ✅ Auto-restore to 0 when button released
            }

            // Return current value if button pressed but not for 3 seconds yet
            return value;
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

        private float CalculateGPSDistance(byte value)
        {
            if (value == 255)
            {
                Dispatcher.Invoke(() =>
                    GpsDistanceText.Text = " Positoin Acquired"
                );
                return 0;
            }

            float distance = value / 10.0f;
            Dispatcher.Invoke(() =>
                GpsDistanceText.Text = $"Position: {distance.ToString("F1")} km"
            );

            return distance;
        }
        private byte GPSSafeDistance(byte value)
        {

            if (value ==0 ) return 1; // 1.5 && value < 255
            else return 0;
        }

        private float CalculateSpeed(byte value)
        {
            
            float speed = (value / 7.0f);
            Dispatcher.Invoke(() =>
                SpeedText.Text = $"Speed: {speed.ToString("F1")} kn"
            );

            return speed;
        }
        private byte SafeSpeed(byte value)
        {
            float speed=CalculateSpeed(value);
            if (speed ==0) return 1; //>5.0f
            else return 0;
        }

        private void HandleSystemFault(int faultState)
        {
            // Only show error if the state has changed
            if (faultState == _lastSystemFaultState)
                return;

            _lastSystemFaultState = faultState;

            if (faultState == 1)
            {
                Dispatcher.Invoke(() =>
                    System.Windows.MessageBox.Show(
                        "Speed feedback faulty",
                        "System Fault",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error
                    )
                );
            }
            else if (faultState == 2)
            {
                Dispatcher.Invoke(() =>
                    System.Windows.MessageBox.Show(
                        "Clutch feedback faulty",
                        "System Fault",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error
                    )
                );
            }
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

            bool isAquiredPos1Pressed = _joystickState.Buttons[2];
            bool isAquiredPos2Pressed = _joystickState.Buttons[3];
            bool shouldAquire = isAquiredPos1Pressed && isAquiredPos2Pressed;
            byte AquireFinal = shouldAquire ? (byte)1 : (byte)0;

            // Button states
            byte start = _joystickState.Buttons[7] ? (byte)1 : (byte)0;
            byte march = (_joystickState.Buttons[8] && throttleValue > 55000) ? (byte)1 : (byte)0;
            byte gps = AquireFinal;
            byte tiltup = _joystickState.Buttons[10] ? (byte)1 : (byte)0;
            byte tiltdown = _joystickState.Buttons[11] ? (byte)1 : (byte)0;
            byte alarm = _joystickState.Buttons[1] ? (byte)1 : (byte)0;
            byte forward = _joystickState.Buttons[15] ? (byte)1 : (byte)0;
            byte backward = _joystickState.Buttons[13] ? (byte)1 : (byte)0;
            byte nutral = _joystickState.Buttons[14] ? (byte)1 : (byte)0;
            byte watchdog = _watchdogEsp;

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
            packet[8] = gps;
            packet[9] = tiltup;
            packet[10] = tiltdown;
            packet[11] = march;
            packet[12] = alarm;
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
                // Reset timers if joystick is lost
                _watchdogPressStartTime = DateTime.MinValue;
                _explodePressStartTime = DateTime.MinValue;

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

            // ✅ Watchdog Logic (Requires 3-second hold)
            byte watchdog = 0;
            if (_joystickState.Buttons[12])
            {
                if (_watchdogPressStartTime == DateTime.MinValue)
                {
                    _watchdogPressStartTime = DateTime.UtcNow;
                }

                if ((DateTime.UtcNow - _watchdogPressStartTime).TotalSeconds >= 3)
                {
                    watchdog = 1;
                }
            }
            else
            {
                _watchdogPressStartTime = DateTime.MinValue;
            }

            // ✅ Explode Logic (Requires 3-second hold)
            bool isExplode1Pressed = _joystickState.Buttons[0];
            bool isExplode2Pressed = _joystickState.Buttons[5];
            bool shouldExplode = isExplode1Pressed && isExplode2Pressed;
            byte explodeFinal = 0;

            if (shouldExplode)
            {
                if (_explodePressStartTime == DateTime.MinValue)
                {
                    _explodePressStartTime = DateTime.UtcNow;
                }

                if ((DateTime.UtcNow - _explodePressStartTime).TotalSeconds >= 3 && _groundStationCard==1)
                {
                    explodeFinal = 1;
                }
            }
            else
            {
                _explodePressStartTime = DateTime.MinValue;
            }

            // Build packet: [0xAA] [0x55] [SEQ] [4 data bytes] [CHECKSUM]
            byte[] packet = new byte[8];

            // Header
            packet[0] = 0xAA;  // Start byte 1
            packet[1] = 0x55;  // Start byte 2
            packet[2] = _espSequence++;  // Sequence number

            // Data payload (4 bytes)
            packet[3] = watchdog;
            packet[4] = explodeFinal;//(byte)(System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.A) ? 1 : 0); 
            packet[5] = SafeSpeed(_speedRelayFeedback);//(byte)(System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.S) ? 1 : 0); //
            packet[6] = GPSSafeDistance(_positionRelayFeedback); //(byte)(System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.D) ? 1 : 0); 

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
            if (data == null || data.Length < 12)
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
            feedback.Position = data[10];  // ✅ This gets forwarded to ESP
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
        public byte Position;
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