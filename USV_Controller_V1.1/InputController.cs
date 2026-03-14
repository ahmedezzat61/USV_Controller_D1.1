using Newtonsoft.Json.Linq;
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
        private const string UsvIp = "192.168.80.66";
        private const int UsvSendPort = 8888;
        private const int UsvReceivePort = 8889;

        // ESP32 Configuration
        private const string EspIp = "192.168.80.69";
        private const int EspPort = 8890;
        private const int EspReceivePort = 8891;  // NEW: ESP feedback port

        // ✅ Feedback values received from USV (shared between threads)
        private volatile byte _positionRelayFeedback = 0;
        private volatile byte _speedRelayFeedback = 0;
        private volatile byte _groundStationCard = 0;

        // ✅ NEW: Separate listener for ESP feedback
        private UdpClient? _espListener;

        // Track last camera velocity to detect changes
        private double _lastPanVelocity = 0;
        private double _lastTiltVelocity = 0;

        // ✅ NEW: Track last acknowledged sequences
        private byte _lastArduinoAck = 0;
        private byte _lastEspAck = 0;

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

                espEndpoint = new IPEndPoint(IPAddress.Parse(EspIp), EspPort);
                espClient = new UdpClient();

                _connected = true;
                _isReachable = arduinoOnline;

                // 🔒 SAFETY RESET ON CONNECT
                UpdateJoystickState();

                Dispatcher.Invoke(() =>
                {
                    IntializedRpm(0);
                });

                // ✅ START THE TIMER
                _timer.Interval = 100;
                _timer.Start();
                _pingTimer.Start();

                Log("⏰ Timer started - sending control packets every 100ms");

                if (arduinoOnline)
                {
                    StartListening(UsvReceivePort);
                    StartEspListening(EspReceivePort);
                }

                Log($"✅ Connected!");
                Log($"📡 USV/Arduino: {UsvIp}:{UsvSendPort} {(arduinoOnline ? "(Online)" : "(Offline - Video Only)")}");
                Log($"📡 ESP32: {EspIp}:{EspPort}");
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

            try { _ptzController?.Stop().Wait(500); } catch { }

            try { usvClient?.Close(); } catch { }
            try { espClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _espListener?.Close(); } catch { }

            _connected = false;
            _isReachable = false;

            _firstPacketSent = false;
            _throttleZeroed = false;

            // ✅ NEW: Reset ESP feedback time and LEDs
            _lastEspFeedbackTime = DateTime.MinValue;
            ResetEspLeds();

            Log("⏹ Streaming stopped.");
            Log("🔌 Disconnected from USV and ESP32.");
            Log("📹 Camera PTZ stopped.");
            Log("💡 ESP status LEDs reset.");
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_connected || _joystick == null || _joystickState == null)
            {
                return;
            }

            UpdateJoystickState();

            if (_joystick == null || _joystickState == null)
            {
                return;
            }
            _watchdogEsp = watchdogEsp(_watchdogEsp);
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

            // ✅ Send ESP packet
            byte[] espPacket = BuildPacket2();

            try
            {
                if (espClient != null && espEndpoint != null)
                {
                    espClient.Send(espPacket, espPacket.Length, espEndpoint);
                }

                Dispatcher.Invoke(() =>
                {
                    string espPreview = "ESP: " + BitConverter.ToString(espPacket).Replace("-", " ");
                    // Uncomment if you want to see ESP packets
                    //UpdatePacket(espPreview);
                });
            }
            catch (Exception ex)
            {
                if (DateTime.Now.Second % 10 == 0)
                {
                    Log($"⚠️ ESP send error: {ex.Message}");
                }
            }

            // ✅ Camera PTZ control via ONVIF using POV Hat
            try
            {
                double panVelocity = 0;
                double tiltVelocity = 0;
                double zoomVelocity = 0;

                int povPosition = _joystickState.PointOfViewControllers[0];

                if (povPosition != -1)
                {
                    double cameraSpeed = 0.5;

                    if (povPosition >= 0 && povPosition <= 4500)
                    {
                        tiltVelocity = cameraSpeed;
                    }
                    else if (povPosition > 4500 && povPosition <= 13500)
                    {
                        panVelocity = cameraSpeed;

                        if (povPosition < 9000)
                            tiltVelocity = cameraSpeed * 0.7;
                        else if (povPosition > 9000)
                            tiltVelocity = -cameraSpeed * 0.7;
                    }
                    else if (povPosition > 13500 && povPosition <= 22500)
                    {
                        tiltVelocity = -cameraSpeed;
                    }
                    else if (povPosition > 22500 && povPosition <= 31500)
                    {
                        panVelocity = -cameraSpeed;

                        if (povPosition < 27000)
                            tiltVelocity = -cameraSpeed * 0.7;
                        else if (povPosition > 27000)
                            tiltVelocity = cameraSpeed * 0.7;
                    }
                    else
                    {
                        tiltVelocity = cameraSpeed;
                    }

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

                bool hasMovement = Math.Abs(panVelocity) > 0.01 ||
                                  Math.Abs(tiltVelocity) > 0.01 ||
                                  Math.Abs(zoomVelocity) > 0.01;

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

        // ✅ UPDATED: Arduino feedback listener with new packet structure
        private async void StartListening(int port = UsvReceivePort)
        {
            try
            {
                _listener = new UdpClient(port);
                Log($"👂 Listening for Arduino feedback on port {port}...");

                while (_connected)
                {
                    var result = await _listener.ReceiveAsync();
                    byte[] data = result.Buffer;

                    // ✅ Parse Arduino feedback with new structure
                    if (ParseArduinoFeedback(data, out ArduinoFeedback feedback))
                    {
                        // Track acknowledgment
                        _lastArduinoAck = feedback.Sequence;

                        // ✅ CRITICAL: Update feedback values immediately!
                        // These values (PositionRelay & SpeedRelay) are automatically forwarded to ESP
                        // in BuildPacket2() which runs every 100ms in Timer_Elapsed
                        _positionRelayFeedback = feedback.Position;  // Forwarded to ESP packet[5]
                        _speedRelayFeedback = feedback.SpeedRelay;        // Forwarded to ESP packet[6]
                        CalculateGPSDistance(feedback.Position);

                        Dispatcher.Invoke(() =>
                        {
                            // Update clutch slider
                            ClutchSlider.Value = ClutchSlider.Maximum + ClutchSlider.Minimum - feedback.Feedback1;

                            if (_joystickState != null)
                            {
                                int throttleRaw = Math.Abs(_joystickState.Z - 65535);
                                double leftRpm = Map(throttleRaw, 0, 65535, 0, 100);
                                double rightRpm = Map(feedback.Feedback2, 5, 400, 0, 100);

                                IntializedRpm(leftRpm);
                                FeedbackRpm(rightRpm);
                            }

                            // Update steering indicator
                            double steeringAngle = (MapSteeringValue(feedback.Feedback3) + 2);
                            double mappedLeft = (steeringAngle + 30) / 60 * (380 - 20) + 20 - (RudderIndicator.Width / 2);
                            Canvas.SetLeft(RudderIndicator, mappedLeft);
                            SteeringValueText.Text = $"{steeringAngle:F1}°";

                            // Update relay indicator
                            RelayIndicator.Fill = feedback.Relay1 ? Brushes.Lime : Brushes.Red;
                            RelayText.Text = feedback.Relay1 ? "ON" : "OFF";
                            RelayText.Foreground = feedback.Relay1 ? Brushes.Lime : Brushes.Red;
                        });

                        // Log acknowledgment
                        //if (DateTime.Now.Millisecond % 500 < 100)
                        //{
                        //    Log($"✅ Arduino ACK: Seq={feedback.Sequence}");
                        //}
                    }
                    else
                    {
                        Log("⚠️ Invalid Arduino feedback packet received");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Arduino listener stopped: {ex.Message}");
            }
        }

        // ✅ NEW: ESP32 feedback listener
        private async void StartEspListening(int port = EspReceivePort)
        {
            try
            {
                _espListener = new UdpClient(port);
                Log($"👂 Listening for ESP32 feedback on port {port}...");

                while (_connected)
                {
                    var result = await _espListener.ReceiveAsync();
                    byte[] data = result.Buffer;

                    // ✅ Parse ESP feedback with new structure
                    if (ParseEspFeedback(data, out EspFeedback feedback))
                    {
                        // Track acknowledgment
                        _lastEspAck = feedback.Sequence;

                        // ✅ Update last feedback time for timeout tracking
                        _lastEspFeedbackTime = DateTime.Now;

                        // ✅ Update the ESP LED indicators on the UI
                        UpdateEspLeds(feedback.Relay1, feedback.Relay2, feedback.Relay3, feedback.Relay4, feedback.Relay5);

                        Dispatcher.Invoke(() =>
                        {
                            // Update a text block with ESP status (if it exists)
                            if (this.FindName("EspStatusText") is TextBlock tbEsp)
                            {
                                tbEsp.Text = $"ESP: R1={feedback.Relay1} R2={feedback.Relay2} R3={feedback.Relay3} R4={feedback.Relay4} R5={feedback.Relay5}";
                            }
                        });

                        // Log acknowledgment periodically
                        //if (DateTime.Now.Millisecond % 500 < 100)
                        //{
                        //    Log($"✅ ESP32 ACK: Seq={feedback.Sequence}, Relays=[{feedback.Relay1},{feedback.Relay2},{feedback.Relay3},{feedback.Relay4},{feedback.Relay5}]");
                        //}
                    }
                    else
                    {
                        Log("⚠️ Invalid ESP32 feedback packet received");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ ESP32 listener stopped: {ex.Message}");
            }
        }
    }
}