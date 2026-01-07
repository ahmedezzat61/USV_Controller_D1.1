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

        // Track last camera velocity to detect changes
        private double _lastPanVelocity = 0;
        private double _lastTiltVelocity = 0;

        private async void ConnectButton_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                Log($"🔍 Checking Arduino/USV connection at {UsvIp}...");

                bool arduinoOnline = false;

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

                usvEndpoint = new IPEndPoint(IPAddress.Parse(UsvIp), UsvSendPort);
                usvClient = new UdpClient();

                _connected = true;
                _isReachable = arduinoOnline;

                // 🔒 SAFETY RESET ON CONNECT
                UpdateJoystickState();

                Dispatcher.Invoke(() =>
                {
                    IntializedRpm(0);   // ALWAYS ZERO
                });


                // ✅ START THE TIMER - after initialization
                _timer.Interval = 100;
                _timer.Start();
                _pingTimer.Start();

                Log("⏰ Timer started - sending control packets every 100ms");

                if (arduinoOnline)
                {
                    StartListening(UsvReceivePort);
                }

                Log($"✅ Connected!");
                Log($"📡 USV/Arduino: {UsvIp}:{UsvSendPort} {(arduinoOnline ? "(Online)" : "(Offline - Video Only)")}");
                Log($"📹 Camera video: {CameraIp}:554 (RTSP)");
                Log($"🎮 Camera PTZ: ONVIF ContinuousMove (Port {CameraOnvifPort})");
                Log($"🕹️ X/Y axes control camera pan/tilt when Button 1 pressed");
                Log($"🎮 Joystick: {(_joystick != null ? "Connected ✅" : "NOT CONNECTED ❌")}");
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

        private void DisconnectButton_Click(object? sender, RoutedEventArgs? e)
        {
            _timer.Stop();
            _pingTimer.Stop();

            // Stop camera PTZ movement
            try { _ptzController?.Stop().Wait(500); } catch { }

            try { usvClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }

            _connected = false;
            _isReachable = false;

            _firstPacketSent = false;
            _throttleZeroed = false;
            Log("⏹ Streaming stopped.");
            Log("🔌 Disconnected from USV.");
            Log("📹 Camera PTZ stopped.");
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // ✅ Check if connected and joystick exists
            if (!_connected || _joystick == null || _joystickState == null)
            {
                return;
            }

            // ✅ Update joystick state
            UpdateJoystickState();

            // ✅ Check again after update (joystick might have disconnected)
            if (_joystick == null || _joystickState == null)
            {
                return; // Joystick disconnected, skip this cycle
            }

            // ✅ Send USV packet
            byte[] usvPacket = BuildPacket();

            try
            {
                if (usvClient != null && usvEndpoint != null)
                {
                    usvClient.Send(usvPacket, usvPacket.Length, usvEndpoint);
                }

                Dispatcher.Invoke(() =>
                {
                    string packetPreview = "USV: " + BitConverter.ToString(usvPacket).Replace("-", " ");

                    if (this.FindName("PacketPreview") is TextBlock tbUsv)
                    {
                        tbUsv.Text = packetPreview;
                    }

                    UpdatePacket(packetPreview);
                });
            }
            catch (Exception ex)
            {
                if (DateTime.Now.Second % 10 == 0)
                {
                    Log($"⚠️ USV send error: {ex.Message}");
                }
            }

            // ✅ Camera PTZ control via ONVIF
            // ✅ Camera PTZ control via ONVIF using POV Hat
            try
            {
                double panVelocity = 0;
                double tiltVelocity = 0;
                double zoomVelocity = 0;

                // ✅ Read POV Hat position
                int povPosition = _joystickState.PointOfViewControllers[0];

                // POV values: -1 = centered, 0 = up, 9000 = right, 18000 = down, 27000 = left
                // Angles are in hundredths of degrees (0-35999)

                if (povPosition != -1) // Hat is being pressed
                {
                    // ✅ Set camera speed (adjust this value for faster/slower movement)
                    double cameraSpeed = 0.5; // 50% speed, range: 0.0 to 1.0

                    // Determine direction based on POV angle
                    if (povPosition >= 0 && povPosition <= 4500)
                    {
                        // Up (0° to 45°)
                        tiltVelocity = cameraSpeed;
                    }
                    else if (povPosition > 4500 && povPosition <= 13500)
                    {
                        // Right (45° to 135°)
                        panVelocity = cameraSpeed;

                        // Diagonal: Up-Right
                        if (povPosition < 9000)
                            tiltVelocity = cameraSpeed * 0.7;
                        // Diagonal: Down-Right
                        else if (povPosition > 9000)
                            tiltVelocity = -cameraSpeed * 0.7;
                    }
                    else if (povPosition > 13500 && povPosition <= 22500)
                    {
                        // Down (135° to 225°)
                        tiltVelocity = -cameraSpeed;
                    }
                    else if (povPosition > 22500 && povPosition <= 31500)
                    {
                        // Left (225° to 315°)
                        panVelocity = -cameraSpeed;

                        // Diagonal: Down-Left
                        if (povPosition < 27000)
                            tiltVelocity = -cameraSpeed * 0.7;
                        // Diagonal: Up-Left
                        else if (povPosition > 27000)
                            tiltVelocity = cameraSpeed * 0.7;
                    }
                    else
                    {
                        // Up (315° to 360°)
                        tiltVelocity = cameraSpeed;
                    }

                    // ✅ Optional: Add zoom control from slider while using hat
                    if (_joystickState.Sliders != null && _joystickState.Sliders.Length > 0)
                    {
                        int sliderValue = _joystickState.Sliders[0];
                        int centered = sliderValue - 32768;

                        if (Math.Abs(centered) > 5000)
                        {
                            zoomVelocity = -(centered / 32768.0);
                            zoomVelocity = Math.Clamp(zoomVelocity, -1.0, 1.0);
                        }
                    }
                }

                // ✅ Check if any camera control is active
                bool hasMovement = Math.Abs(panVelocity) > 0.01 ||
                                  Math.Abs(tiltVelocity) > 0.01 ||
                                  Math.Abs(zoomVelocity) > 0.01;

                // ✅ Send command if movement detected or if we need to stop
                if (_ptzController != null)
                {
                    if (hasMovement)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _ptzController.ContinuousMove(panVelocity, tiltVelocity, zoomVelocity);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"ContinuousMove error: {ex.Message}");
                            }
                        });

                        _lastPanVelocity = panVelocity;
                        _lastTiltVelocity = tiltVelocity;
                    }
                    else if (_lastPanVelocity != 0 || _lastTiltVelocity != 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _ptzController.Stop();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Stop error: {ex.Message}");
                            }
                        });

                        _lastPanVelocity = 0;
                        _lastTiltVelocity = 0;
                    }
                }

                // Update UI
                Dispatcher.Invoke(() =>
                {
                    if (this.FindName("PacketPreviewCamera") is TextBlock tbCam)
                    {
                        string hatStatus = povPosition == -1 ? "CENTERED" : $"{povPosition / 100}°";
                        tbCam.Text = $"CAM [Hat:{hatStatus}]: Pan={panVelocity:F2}, Tilt={tiltVelocity:F2}, Zoom={zoomVelocity:F2}";
                    }
                });
            }
            catch (Exception ex)
            {
                if (DateTime.Now.Second % 5 == 0)
                {
                    Log($"⚠️ Camera PTZ error: {ex.Message}");
                }
            }
        }

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
                            ClutchSlider.Value = ClutchSlider.Maximum + ClutchSlider.Minimum - feedback1;

                            // ✅ CHANGED: Use Z-axis (throttle) instead of LeftThumbY
                            // Get throttle position (reversed: 0=max, 65535=min)
                            //if (_joystickState != null)
                            //{
                            int throttleRaw = Math.Abs(_joystickState.Z- 65535);

                            //    double increment = (65535 - throttleRaw) / 20000.0;
                            //    X += increment;
                            //    X = Math.Clamp(X, 0, 450);
                            //}

                            double leftRpm = Map(throttleRaw, 0, 65535, 0, 100);
                            double rightRpm = Map(feedback2, 5, 400, 0, 100);

                            IntializedRpm(leftRpm);
                            FeedbackRpm(rightRpm);

                            // Update steering indicator
                            double steeringAngle = (MapSteeringValue(feedback3) + 2);
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