using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace USV_Controller_V1._1
{
    // Partial class - network/controller handling (moved from MainWindow)
    public partial class MainWindow
    {
        // ✅ USV/Arduino Configuration
        private const string UsvIp = "192.168.0.177";      // Arduino IP when connected
        private const int UsvSendPort = 8888;               // Arduino RECEIVES control on 8888
        private const int UsvReceivePort = 8889;            // Arduino SENDS feedback on 8889

        // ✅ Camera Configuration
        // Camera IP: 192.168.0.50
        // - Video Stream (RTSP): Port 554 (receiving video FROM camera)
        // - ONVIF Control: Port 2020 (for camera config/PTZ via ONVIF)
        // 
        // NOTE: Since you're not connected to Arduino right now, camera control
        // won't work until Arduino is connected. Camera VIDEO should still work though!
        //
        // Camera control goes to Arduino (when connected), NOT directly to camera

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ⚠️ NOTE: Arduino/USV must be powered on and connected for control to work
                // Camera video will work independently even if Arduino is offline

                Log($"🔍 Checking Arduino/USV connection at {UsvIp}...");

                bool arduinoOnline = false;

                // Ping Arduino/USV before connecting
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var reply = await ping.SendPingAsync(IPAddress.Parse(UsvIp), 1000);
                    arduinoOnline = reply.Status == System.Net.NetworkInformation.IPStatus.Success;

                    if (!arduinoOnline)
                    {
                        Log($"⚠️ Arduino/USV at {UsvIp} not reachable.");

                        var result = MessageBox.Show(
                            $"Cannot reach Arduino/USV at {UsvIp}\n\n" +
                            $"Camera video will still work, but you won't be able to:\n" +
                            $"• Control USV motors/steering\n" +
                            $"• Control camera pan/tilt\n" +
                            $"• Receive feedback data\n\n" +
                            $"Do you want to continue anyway?\n" +
                            $"(Useful for testing camera video only)",
                            "Arduino/USV Not Connected",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.No)
                            return;

                        Log("⚠️ Continuing without Arduino - VIDEO ONLY mode");
                    }
                    else
                    {
                        Log($"✅ Arduino/USV is online and reachable");
                    }
                }

                // ✅ Setup endpoints
                // Arduino/USV receives control packets (only if connected)
                usvEndpoint = new IPEndPoint(IPAddress.Parse(UsvIp), UsvSendPort);

                // Camera control is now via ONVIF, not UDP
                // No need for camEndpoint anymore

                usvClient = new UdpClient();
                // camClient not needed anymore - using ONVIF instead

                _connected = true;
                _isReachable = arduinoOnline;

                // ✅ START THE TIMER - This is critical!
                _timer.Interval = 100;  // 10 Hz - sends packets every 100ms
                _timer.Start();
                _pingTimer.Start();

                Log("⏰ Timer started - sending control packets every 100ms");

                // Only start listening if Arduino is online
                if (arduinoOnline)
                {
                    StartListening(UsvReceivePort);
                }

                Log($"✅ Connected!");
                Log($"📡 USV/Arduino: {UsvIp}:{UsvSendPort} {(arduinoOnline ? "(Online)" : "(Offline - Video Only)")}");
                Log($"📹 Camera video: {CameraIp}:554 (RTSP)");
                Log($"🎮 Camera PTZ: {CameraIp}:{CameraOnvifPort} (ONVIF)");
                Log($"🕹️ Right stick controls camera pan/tilt via ONVIF");
                Log($"🎮 Xbox Controller: {(_controller.IsConnected ? "Connected ✅" : "NOT CONNECTED ❌")}");
            }
            catch (Exception ex)
            {
                Log($"❌ Connection error: {ex.Message}");
                _connected = false;
                MessageBox.Show(
                    $"Connection failed:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _pingTimer.Stop();

            // Stop camera PTZ movement
            try { _ptzController?.Stop().Wait(500); } catch { }

            try { usvClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }

            _connected = false;
            _isReachable = false;

            Log("⏹ Streaming stopped.");
            Log("🔌 Disconnected from USV.");
            Log("📹 Camera PTZ stopped.");
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // Removed annoying timer tick messages - they were for debugging only

            if (!_connected || !_controller.IsConnected)
            {
                return;
            }

            _gamepad = _controller.GetState().Gamepad;

            // ✅ Send USV packet (only if Arduino is connected)
            byte[] usvPacket = BuildPacket();

            try
            {
                if (usvClient != null && usvEndpoint != null)
                {
                    usvClient.Send(usvPacket, usvPacket.Length, usvEndpoint);
                }

                Dispatcher.Invoke(() =>
                {
                    if (this.FindName("PacketPreview") is TextBlock tbUsv)
                    {
                        tbUsv.Text = "USV: " + BitConverter.ToString(usvPacket).Replace("-", " ");
                    }
                });
            }
            catch (Exception ex)
            {
                // Silently fail if Arduino not connected
                // Only log errors occasionally to avoid spam
                if (DateTime.Now.Second % 10 == 0)
                {
                    Log($"⚠️ USV send error: {ex.Message}");
                }
            }

            // ✅ Camera PTZ control via ONVIF (works independently of Arduino)
            try
            {
                // Read raw Xbox stick values
                short rawPanX = _gamepad.RightThumbX;
                short rawTiltY = _gamepad.RightThumbY;

                // ✅ FIX: Handle the -32768 edge case to avoid math error
                // Xbox stick range is -32768 to 32767, but we can't negate -32768
                // So we clamp it to -32767
                if (rawPanX == -32768) rawPanX = -32767;
                if (rawTiltY == -32768) rawTiltY = -32767;

                // Convert Xbox stick values to ONVIF velocity (-1.0 to 1.0)
                double panVelocity = rawPanX / 32767.0;  // Now safe: -1.0 to 1.0
                double tiltVelocity = rawTiltY / 32767.0; // Now safe: -1.0 to 1.0

                // Apply deadzone (reduced from 0.1 to 0.08 for better sensitivity)
                if (Math.Abs(panVelocity) < 0.08) panVelocity = 0;
                if (Math.Abs(tiltVelocity) < 0.08) tiltVelocity = 0;

                

                // Send ONVIF command when stick is moved
                if (_ptzController != null && (Math.Abs(panVelocity) > 0 || Math.Abs(tiltVelocity) > 0))
                {
                    // Actually await the task to catch errors
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool success = await _ptzController.ContinuousMove(panVelocity, tiltVelocity);
                            if (!success && DateTime.Now.Millisecond < 100)
                            {
                                Log($"⚠️ PTZ command failed - check Output window for details");
                            }
                        }
                        catch (Exception ex)
                        {
                            if (DateTime.Now.Millisecond < 100)
                            {
                                Log($"❌ PTZ error: {ex.Message}");
                            }
                        }
                    });

                    // Log PTZ command being sent
                    if (DateTime.Now.Millisecond < 100)
                    {
                        Log($"📹 Sending PTZ command: Pan={panVelocity:F2}, Tilt={tiltVelocity:F2}");
                    }
                }
                else if (_ptzController != null && (Math.Abs(_currentPanVelocity) > 0 || Math.Abs(_currentTiltVelocity) > 0))
                {
                    // Stop camera when stick is released
                    var task = _ptzController.Stop();
                    _currentPanVelocity = 0;
                    _currentTiltVelocity = 0;
                }

                // Update UI
                Dispatcher.Invoke(() =>
                {
                    if (this.FindName("PacketPreviewCamera") is TextBlock tbCam)
                    {
                        tbCam.Text = $"CAM: Pan={panVelocity:F2} ({rawPanX}), Tilt={tiltVelocity:F2} ({rawTiltY})";
                    }
                });
            }
            catch (Exception ex)
            {
                // Log camera control errors
                if (DateTime.Now.Second % 5 == 0) // Log every 5 seconds
                {
                    Log($"⚠️ Camera PTZ error: {ex.Message}");
                }
            }
        }

        // Track camera velocity to stop when released
        private double _currentPanVelocity = 0;
        private double _currentTiltVelocity = 0;

        private async void PingTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_connected || usvEndpoint == null) return;

            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var reply = await ping.SendPingAsync(usvEndpoint.Address, 1000);
                    _isReachable = reply.Status == System.Net.NetworkInformation.IPStatus.Success;

                    if (!_isReachable)
                    {
                        Log("⚠️ Warning: USV unreachable!");
                    }
                }
            }
            catch (Exception)
            {
                _isReachable = false;
            }
        }

        private async void StartListening(int port = UsvReceivePort)
        {
            try
            {
                _listener = new UdpClient(port);
                Log($"👂 Listening for USV feedback on port {port}...");

                while (_connected)
                {
                    var result = await _listener.ReceiveAsync();
                    byte[] data = result.Buffer;

                    if (data.Length == 7)
                    {
                        int feedback1 = (data[0] << 8) | data[1];
                        int feedback2 = (data[2] << 8) | data[3];
                        int feedback3 = (data[4] << 8) | data[5];
                        bool relay1 = data[6] == 1;

                        Dispatcher.Invoke(() =>
                        {
                            // Update clutch slider
                            ClutchSlider.Value = ClutchSlider.Maximum - feedback1;

                            // Update RPM gauges
                            short yAxisLive = _gamepad.LeftThumbY;
                            double increment = yAxisLive / 3000.0;
                            X += increment;
                            X = Math.Clamp(X, 0, 450);

                            double leftRpm = Map(X, 0, 450, 800, 4000);
                            double rightRpm = Map(feedback2, 0, 450, 800, 4000);

                            IntializedRpm(leftRpm);
                            FeedbackRpm(rightRpm);

                            // Update steering indicator
                            double steeringAngle = MapSteeringValue(feedback3);
                            double mappedLeft = (steeringAngle + 30) / 60 * (380 - 20) + 20 - (RudderIndicator.Width / 2);
                            Canvas.SetLeft(RudderIndicator, mappedLeft);
                            SteeringValueText.Text = $"{steeringAngle:F1}°";

                            // Update relay indicator
                            RelayIndicator.Fill = relay1 ? Brushes.Lime : Brushes.Red;
                            RelayText.Text = relay1 ? "ON" : "OFF";
                            RelayText.Foreground = relay1 ? Brushes.Lime : Brushes.Red;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Listener stopped: {ex.Message}");
            }
        }
    }
}