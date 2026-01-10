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

        // ✅ Scale DirectInput axis (0-65535) to your required range (0-1023)
        private int ScaleAxis(int value)
        {
            // Map [0, 65535] → [0, 1023]
            return (int)((value / 65535.0) * 1023.0);
        }

        // ✅ Scale DirectInput axis REVERSED (for throttle)
        // When hand forward (value near 0) → maximum speed (1023)
        // When hand back (value near 65535) → minimum speed (0)
        private int ScaleAxisReversed(int value)
        {
            // Map [0, 65535] → [1023, 0] (reversed)
            return 1023 - (int)((value / 65535.0) * 1023.0);
        }

        // ✅ Scale centered axis (like rudder) to 0-1023 range
        // Rudder: Left (-32768) → 0, Center (0) → 512, Right (32767) → 1023
        private int ScaleCenteredAxis(int value)
        {
            // DirectInput gives 0-65535, center at 32768
            // Convert to [-32768, 32767] range first
            int centered = value - 32768;
            
            // Map [-32768, 32767] → [0, 1023]
            return (int)((centered + 32768) / 64.0);
        }

        // ✅ Scale centered axis to signed byte for camera control
        // Maps centered joystick axis (0-65535, center 32768) to [-127, 127]
        private sbyte ScaleAxisToSignedByte(int value, int deadzone = 3000)
        {
            // Center the value: 0-65535 → -32768 to 32767
            int centered = value - 32768;
            
            // Apply deadzone
            if (Math.Abs(centered) < deadzone) return 0;
            
            // Map [-32768, 32767] → [-127, 127]
            double normalized = centered >= 0 ? centered / 32767.0 : centered / 32768.0;
            int scaled = (int)Math.Round(normalized * 127.0);
            scaled = Math.Clamp(scaled, -127, 127);
            return (sbyte)scaled;
        }

        private double ScaleSliderToZoom(int value, int deadzone = 5000)
        {
            // Center the value: 0-65535 → -32768 to 32767
            int centered = value - 32768;

            // Apply deadzone
            if (Math.Abs(centered) < deadzone) return 0;

            // Map [-32768, 32767] → [1.0, -1.0] (inverted: forward = zoom in = positive)
            double normalized = centered >= 0 ? centered / 32767.0 : centered / 32768.0;

            // Invert so forward slider = positive (zoom in)
            return -Math.Clamp(normalized, -1.0, 1.0);
        }

        
        // ✅ CORRECTED: USV packet with throttle on Z-axis
        private byte[] BuildPacket()
        {
            // ✅ Safety check for disconnected joystick
            if (_joystickState == null)
            {
                // Return neutral packet if joystick disconnected
                return new byte[14]
                {
            0x03, 0xFF, // speedAxis = 1023 (zero throttle for safety)
            0x02, 0x00, // steeringAxis = 512 (center rudder)
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };
            }

            int throttleValue = _joystickState.Z;

            // ✅ Safety check: Operator must move throttle to minimum first
            if (!_throttleZeroed)
            {
                // Check if throttle is at minimum (near 65535)
                if (throttleValue > 60000)  // Within ~8% of minimum
                {
                    _throttleZeroed = true;
                    Dispatcher.Invoke(() => Log("✅ Throttle zeroed - control enabled"));
                }
                else
                {
                    // Force to zero until operator manually zeros throttle
                    throttleValue = 65535;
                }
            }

            // 🎮 CORRECT AXIS MAPPING:
            // Z-Axis = Throttle slider (REVERSED: forward = max speed)
            // RotationZ = Rudder (left/right steering)

            // ✅ Throttle on Z-axis (reversed)
            int speedAxis = ScaleAxisReversed(throttleValue);
            speedAxis = Math.Clamp(speedAxis, 0, 1023);
            
            // ✅ Rudder control on RotationZ axis (twist/yaw)
            int steeringAxis = ScaleCenteredAxis(_joystickState.RotationZ);
            steeringAxis = Math.Clamp(steeringAxis, 0, 1023);
            
            // Clutch buttons (buttons 0, 2, 3 - skipping 1 since it's for camera)
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

            byte[] packet = new byte[14];
            packet[0] = (byte)(speedAxis >> 8);     // ✅ Throttle (Z-axis) high byte
            packet[1] = (byte)(speedAxis & 0xFF);   // ✅ Throttle (Z-axis) low byte
            packet[2] = (byte)(steeringAxis >> 8);     // Rudder (RotationZ) high byte
            packet[3] = (byte)(steeringAxis & 0xFF);   // Rudder (RotationZ) low byte
            packet[4] = start;     
            packet[5] = b;    
            packet[6] = tiltup;      
            packet[7] = tiltdown;      
            packet[8] = march;     
            packet[9] = rb;   
            packet[10] = backward; 
            packet[11] = forward;    
            packet[12] = nutral; 
            packet[13] = watchdog; 
            
            return packet;

        }

        private byte[] BuildPacket2()
        {


           
            byte watchdog = _joystickState.Buttons[12] ? (byte)1 : (byte)0;
            // 1. Keep them as booleans for the logic
            bool isExplode1Pressed = _joystickState.Buttons[0];
            bool isExplode2Pressed = _joystickState.Buttons[5];

            // 2. Use the logical AND operator (&&)
            bool shouldExplode = isExplode1Pressed && isExplode2Pressed;

            // 3. Convert to byte only if needed for the final output
            byte explodeFinal = shouldExplode ? (byte)1 : (byte)0;


            byte[] packet = new byte[4];
            packet[0] = watchdog;    
            packet[1] = explodeFinal;
            packet[2] = _positionRelayFeedback;
            packet[3] = _speedRelayFeedback;



            return packet;



        }







    }
}