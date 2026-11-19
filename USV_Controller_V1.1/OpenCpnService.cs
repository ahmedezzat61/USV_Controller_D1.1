using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace USV_Controller_V1._1
{
    public class OpenCpnService
    {
        private const int NMEA_PORT = 10110;

        public double LatitudeDeg { get; private set; } = 0.0;
        public double LongitudeDeg { get; private set; } = 0.0;
        public bool HasFix { get; private set; } = false;

        public int LatInt => (int)Math.Round(LatitudeDeg * 1_000_000.0);
        public int LonInt => (int)Math.Round(LongitudeDeg * 1_000_000.0);

        public event Action<double, double>? WaypointReceived;

        private TcpListener? _server;
        private CancellationTokenSource _cts = new();
        private readonly object _serverLock = new();
        private readonly Action<string> _log;

        public OpenCpnService(Action<string> log)
        {
            _log = log;
        }

        public void Start()
        {
            lock (_serverLock)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            CancellationToken token = _cts.Token;
            Task.Run(() => ListenLoop(token));
            _log("🗺️ OpenCPN NMEA listener started on port " + NMEA_PORT);
        }

        public void Stop()
        {
            lock (_serverLock)
            {
                _cts.Cancel();
                try { _server?.Stop(); } catch { }
                _server = null;
            }
            _log("🗺️ OpenCPN NMEA listener stopped.");
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            TcpListener? server = null;
            try
            {
                if (ct.IsCancellationRequested) return;

                lock (_serverLock)
                {
                    if (ct.IsCancellationRequested) return;
                    server = new TcpListener(IPAddress.Any, NMEA_PORT);
                    server.Start();
                    _server = server;
                }

                while (!ct.IsCancellationRequested)
                {
                    var client = await server.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleClient(client, ct), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log($"❌ OpenCPN listener error: {ex.Message}");
            }
            finally
            {
                lock (_serverLock)
                {
                    if (ReferenceEquals(_server, server))
                    {
                        try { _server?.Stop(); } catch { }
                        _server = null;
                    }
                }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct)
        {
            _log("🗺️ OpenCPN connected to NMEA listener");
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            var leftover = "";

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break;

                    string data = leftover + Encoding.ASCII.GetString(buffer, 0, n);
                    string[] lines = data.Split('\n');
                    leftover = lines[^1];

                    foreach (var raw in lines[..^1])
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("$GPWPL") || line.StartsWith("$ECWPL"))
                            ParseWPL(line);
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"⚠️ OpenCPN client read error: {ex.Message}");
            }
        }

        private void ParseWPL(string sentence)
        {
            try
            {
                int star = sentence.IndexOf('*');
                string body = star >= 0 ? sentence[..star] : sentence;
                string[] p = body.Split(',');

                if (p.Length < 5) return;

                double lat = NmeaToDeg(p[1], p[2]);
                double lon = NmeaToDeg(p[3], p[4]);
                string name = p.Length > 5 ? p[5] : "WPT";

                LatitudeDeg = lat;
                LongitudeDeg = lon;
                HasFix = true;

                _log($"🗺️ Waypoint [{name}]: {lat:F6}°, {lon:F6}° → int({LatInt}, {LonInt})");
                WaypointReceived?.Invoke(lat, lon);
            }
            catch (Exception ex)
            {
                _log($"⚠️ NMEA parse error: {ex.Message}");
            }
        }

        private static double NmeaToDeg(string value, string hemi)
        {
            double raw = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            int deg = (int)(raw / 100);
            double mins = raw - deg * 100.0;
            double result = deg + mins / 60.0;
            if (hemi == "S" || hemi == "W") result = -result;
            return result;
        }
    }
}