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
        private const int UsvWaypointPort = 8889;  // ✅ TCP waypoint port

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
                Log($"📹 Camera video: {CameraIp}:{CameraPort} (Hikvision SDK)");
                Log($"🎮 Camera PTZ: Hikvision SDK PTZControllWithSpeed");
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

            try { _ptzController?.PTZStop(); } catch { }

            try { usvClient?.Close(); } catch { }
            try { espClient?.Close(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _espListener?.Close(); } catch { }
            try { _waypointTcpClient?.Close(); _waypointTcpClient = null; } catch { }
            _waypointSent = false;

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
            // ✅ USV/joystick requires connection
            if (!_connected || _joystick == null || _joystickState == null)
            {
                // ✅ PTZ still works even without USV connection
                TryControlPTZ();
                return;
            }

            UpdateJoystickState();

            if (_joystick == null || _joystickState == null) return;

            _watchdogEsp = watchdogEsp(_watchdogEsp);

            // ✅ MODE-AWARE SENDING
            if (_controlMode == ControlMode.Manual)
            {
                // Send USV packet via UDP (existing behavior)
                byte[] usvPacket = BuildPacket();
                try
                {
                    if (usvClient != null && usvEndpoint != null)
                        usvClient.Send(usvPacket, usvPacket.Length, usvEndpoint);

                    Dispatcher.Invoke(() =>
                    {
                        string packetPreview = "USV: " + BitConverter.ToString(usvPacket).Replace("-", " ");
                        if (this.FindName("PacketPreview") is TextBlock tbUsv)
                            tbUsv.Text = packetPreview;
                        UpdatePacket(packetPreview);
                    });
                }
                catch (Exception ex)
                {
                    if (DateTime.Now.Second % 10 == 0)
                        Log($"⚠️ USV send error: {ex.Message}");
                }
            }
            else if (_controlMode == ControlMode.Waypoint && !_waypointSent)
            {
                // ✅ Send waypoint TCP packet ONCE
                if (_openCpnService != null && _openCpnService.HasFix && !_waypointSendInProgress)
                {
                    int latInt = _openCpnService.LatInt;
                    int lonInt = _openCpnService.LonInt;
                    byte[] wpPacket = BuildWaypointPacket(latInt, lonInt);
                    _waypointSendInProgress = true;
                    _ = SendWaypointAsync(wpPacket, latInt, lonInt);
                }
                else
                {
                    if (!_waypointSendInProgress && DateTime.Now.Second % 5 == 0)
                        Log("⚠️ Waypoint mode active but no waypoint received from OpenCPN yet");
                }
            }
            if (_controlMode != ControlMode.Idle)
            {
                byte[] espPacket = BuildPacket2();
                try
                {
                    if (espClient != null && espEndpoint != null)
                        espClient.Send(espPacket, espPacket.Length, espEndpoint);
                }
                catch (Exception ex)
                {
                    if (DateTime.Now.Second % 10 == 0)
                        Log($"⚠️ ESP send error: {ex.Message}");
                }
            }

            // ✅ PTZ runs always
            TryControlPTZ();
        }

        private async Task SendWaypointAsync(byte[] packet, int latInt, int lonInt)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient
                {
                    SendTimeout = 3000
                };
                _waypointTcpClient = client;

                Task connectTask = client.ConnectAsync(UsvIp, UsvWaypointPort);
                Task completed = await Task.WhenAny(connectTask, Task.Delay(3000));
                if (completed != connectTask)
                    throw new TimeoutException("TCP waypoint connection timed out");

                await connectTask;
                await client.GetStream().WriteAsync(packet, 0, packet.Length);

                bool stillLatestWaypoint =
                    _openCpnService != null &&
                    _openCpnService.HasFix &&
                    _openCpnService.LatInt == latInt &&
                    _openCpnService.LonInt == lonInt;

                if (!_connected || _controlMode != ControlMode.Waypoint || !stillLatestWaypoint)
                    return;

                _waypointSent = true;

                double latDeg = latInt / 1000000.0;
                double lonDeg = lonInt / 1000000.0;
                Dispatcher.Invoke(() =>
                {
                    string preview = "WPT TCP: " + BitConverter.ToString(packet).Replace("-", " ");
                    Log($"✅ Waypoint sent via TCP → {UsvIp}:{UsvWaypointPort}");
                    Log($"   Lat: {latDeg:F6}° ({latInt}), Lon: {lonDeg:F6}° ({lonInt})");
                    if (this.FindName("PacketPreview") is TextBlock tbUsv)
                        tbUsv.Text = preview;
                    UpdatePacket(preview);
                });
            }
            catch (Exception ex)
            {
                _waypointSent = false;
                Log($"❌ Waypoint TCP send error: {ex.Message}");
            }
            finally
            {
                _waypointSendInProgress = false;
                if (client != null)
                {
                    try { client.Close(); } catch { }
                }
                if (ReferenceEquals(_waypointTcpClient, client))
                    _waypointTcpClient = null;
            }
        }

        // ✅ NEW: Extracted PTZ logic into its own method — runs regardless of USV connection
        private void TryControlPTZ()
        {
            if (_joystick == null || _joystickState == null) return;
            if (_ptzController == null || !_ptzController.IsConnected) return;

            try
            {
                UpdateJoystickState();
                if (_joystickState == null) return;

                int povPosition = _joystickState.PointOfViewControllers[0];
                uint ptzCommand = 0;
                bool shouldMove = false;

                if (povPosition != -1)
                {
                    if (povPosition >= 0 && povPosition <= 4500)
                    {
                        ptzCommand = CHCNetSDK.TILT_UP;
                        shouldMove = true;
                    }
                    else if (povPosition > 4500 && povPosition <= 13500)
                    {
                        if (povPosition < 9000)
                            ptzCommand = CHCNetSDK.UP_RIGHT;
                        else if (povPosition > 9000)
                            ptzCommand = CHCNetSDK.DOWN_RIGHT;
                        else
                            ptzCommand = CHCNetSDK.PAN_RIGHT;
                        shouldMove = true;
                    }
                    else if (povPosition > 13500 && povPosition <= 22500)
                    {
                        ptzCommand = CHCNetSDK.TILT_DOWN;
                        shouldMove = true;
                    }
                    else if (povPosition > 22500 && povPosition <= 31500)
                    {
                        if (povPosition < 27000)
                            ptzCommand = CHCNetSDK.DOWN_LEFT;
                        else if (povPosition > 27000)
                            ptzCommand = CHCNetSDK.UP_LEFT;
                        else
                            ptzCommand = CHCNetSDK.PAN_LEFT;
                        shouldMove = true;
                    }
                }

                if (shouldMove)
                {
                    _ptzController.PTZMove(ptzCommand, 4);
                    _lastPanVelocity = 1;
                }
                else if (_lastPanVelocity != 0)
                {
                    _ptzController.PTZStop();
                    _lastPanVelocity = 0;
                }

                Dispatcher.Invoke(() =>
                {
                    if (this.FindName("PacketPreviewCamera") is TextBlock tbCam)
                    {
                        string hatStatus = povPosition == -1 ? "CENTERED" : $"{_joystickState.PointOfViewControllers[0] / 100}°";
                        string action = shouldMove ? $"Moving (Cmd {ptzCommand})" : "Stopped";
                        tbCam.Text = $"CAM [Hat:{hatStatus}]: {action}";
                    }
                });
            }
            catch (Exception ex)
            {
                if (DateTime.Now.Second % 5 == 0)
                    Log($"⚠️ Camera PTZ error: {ex.Message}");
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