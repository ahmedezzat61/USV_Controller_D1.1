using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace USV_Controller_V1._1
{
    /// <summary>
    /// ONVIF PTZ controller for camera pan/tilt control
    /// Uses HTTP/SOAP to send ONVIF commands to camera
    /// </summary>
    public class OnvifPtzController
    {
        private readonly string _cameraIp;
        private readonly int _onvifPort;
        private readonly string _username;
        private readonly string _password;
        private readonly HttpClient _httpClient;

        // Current velocity for continuous movement
        private double _currentPanVelocity = 0;
        private double _currentTiltVelocity = 0;

        public OnvifPtzController(string cameraIp, int onvifPort, string username, string password)
        {
            _cameraIp = cameraIp;
            _onvifPort = onvifPort;
            _username = username;
            _password = password;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        }

        /// <summary>
        /// Send continuous PTZ movement command
        /// </summary>
        /// <param name="panVelocity">Pan velocity: -1.0 (left) to 1.0 (right)</param>
        /// <param name="tiltVelocity">Tilt velocity: -1.0 (down) to 1.0 (up)</param>
        public async Task<bool> ContinuousMove(double panVelocity, double tiltVelocity)
        {
            try
            {
                // Only send if velocity changed significantly (avoid spam)
                if (Math.Abs(panVelocity - _currentPanVelocity) < 0.05 &&
                    Math.Abs(tiltVelocity - _currentTiltVelocity) < 0.05)
                {
                    return true; // No change needed
                }

                _currentPanVelocity = panVelocity;
                _currentTiltVelocity = tiltVelocity;

                // Try multiple profile tokens that cameras commonly use
                var profileTokens = new[] { "000", "profile_1", "profile1", "Profile_1", "MainProfile" };

                // Add basic authentication header once
                var byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                // Try multiple possible PTZ service URLs
                var urls = new[]
                {
                        $"http://{_cameraIp}:{_onvifPort}/onvif/ptz",
                        $"http://{_cameraIp}:{_onvifPort}/onvif/PTZ",
                        $"http://{_cameraIp}:{_onvifPort}/onvif/ptz_service",
                        $"http://{_cameraIp}:80/onvif/ptz"
                    };

                string lastError = "";

                foreach (var profileToken in profileTokens)
                {
                    // Try ONVIF v2.0 format first
                    string soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
    <s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" 
                xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
                xmlns:tt=""http://www.onvif.org/ver10/schema"">
      <s:Body>
        <tptz:ContinuousMove>
          <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
          <tptz:Velocity>
            <tt:PanTilt x=""{panVelocity:F2}"" y=""{tiltVelocity:F2}"" space=""http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace""/>
          </tptz:Velocity>
        </tptz:ContinuousMove>
      </s:Body>
    </s:Envelope>";

                    var content = new StringContent(soapEnvelope, Encoding.UTF8, "application/soap+xml");

                    foreach (var url in urls)
                    {
                        try
                        {
                            var response = await _httpClient.PostAsync(url, content);

                            if (response.IsSuccessStatusCode)
                            {
                                // Success! Log for debugging
                                System.Diagnostics.Debug.WriteLine($"✅ PTZ Success: {url} with profile {profileToken}");
                                return true;
                            }

                            // Store error for debugging
                            var responseText = await response.Content.ReadAsStringAsync();
                            lastError = $"URL: {url}, Profile: {profileToken}, Status: {response.StatusCode}, Response: {responseText.Substring(0, Math.Min(200, responseText.Length))}";

                            System.Diagnostics.Debug.WriteLine($"❌ Failed: {lastError}");

                            if (responseText.Contains("profile") || responseText.Contains("Profile"))
                            {
                                // Wrong profile token, try next one
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastError = $"URL: {url}, Profile: {profileToken}, Exception: {ex.Message}";
                            System.Diagnostics.Debug.WriteLine($"❌ Exception: {lastError}");
                            // Try next URL
                            continue;
                        }
                    }

                    // Try alternate ONVIF v1.0 format
                    string soapEnvelopeV1 = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
    <soap:Envelope xmlns:soap=""http://www.w3.org/2003/05/soap-envelope""
                   xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
                   xmlns:tt=""http://www.onvif.org/ver10/schema"">
      <soap:Body>
        <tptz:ContinuousMove>
          <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
          <tptz:Velocity>
            <tt:PanTilt x=""{panVelocity:F2}"" y=""{tiltVelocity:F2}""/>
          </tptz:Velocity>
        </tptz:ContinuousMove>
      </soap:Body>
    </soap:Envelope>";

                    var contentV1 = new StringContent(soapEnvelopeV1, Encoding.UTF8, "application/soap+xml");

                    foreach (var url in urls)
                    {
                        try
                        {
                            var response = await _httpClient.PostAsync(url, contentV1);

                            if (response.IsSuccessStatusCode)
                            {
                                System.Diagnostics.Debug.WriteLine($"✅ PTZ Success (V1): {url} with profile {profileToken}");
                                return true;
                            }
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                // Log failure for debugging
                System.Diagnostics.Debug.WriteLine($"❌ PTZ Failed - Last error: {lastError}");

                // If we get here, all attempts failed
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ PTZ Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stop PTZ movement
        /// </summary>
        public async Task<bool> Stop()
        {
            try
            {
                _currentPanVelocity = 0;
                _currentTiltVelocity = 0;

                var profileTokens = new[] { "000", "profile_1", "profile1", "Profile_1", "MainProfile" };

                var byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                var urls = new[]
                {
                        $"http://{_cameraIp}:{_onvifPort}/onvif/ptz",
                        $"http://{_cameraIp}:{_onvifPort}/onvif/PTZ",
                        $"http://{_cameraIp}:{_onvifPort}/onvif/ptz_service",
                        $"http://{_cameraIp}:80/onvif/ptz"
                    };

                foreach (var profileToken in profileTokens)
                {
                    string soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
    <s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" 
                xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl"">
      <s:Body>
        <tptz:Stop>
          <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
          <tptz:PanTilt>true</tptz:PanTilt>
        </tptz:Stop>
      </s:Body>
    </s:Envelope>";

                    var content = new StringContent(soapEnvelope, Encoding.UTF8, "application/soap+xml");

                    foreach (var url in urls)
                    {
                        try
                        {
                            var response = await _httpClient.PostAsync(url, content);
                            if (response.IsSuccessStatusCode)
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}

//namespace USV_Controller_V1._1
//{
//    public class TapoPtzController : IDisposable
//    {
//        private readonly string _cameraIp;
//        private readonly string _username;
//        private readonly string _password;
//        private readonly HttpClient _httpClient;
//        private string _sessionCookie;

//        public TapoPtzController(string cameraIp, string username, string password)
//        {
//            _cameraIp = cameraIp;
//            _username = username;
//            _password = password;
//            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
//        }

//        /// <summary>
//        /// Authenticate with the camera and store session cookie
//        /// </summary>
//        public async Task<bool> LoginAsync()
//        {
//            var loginPayload = new
//            {
//                method = "login",
//                @params = new
//                {
//                    username = _username,
//                    password = _password
//                }
//            };

//            var content = new StringContent(JsonConvert.SerializeObject(loginPayload), Encoding.UTF8, "application/json");
//            var response = await _httpClient.PostAsync($"http://{_cameraIp}/", content);

//            if (!response.IsSuccessStatusCode) return false;

//            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
//            {
//                foreach (var cookie in cookies)
//                {
//                    if (cookie.Contains("TP_SESSIONID"))
//                    {
//                        _sessionCookie = cookie.Split(';')[0];
//                        _httpClient.DefaultRequestHeaders.Add("Cookie", _sessionCookie);
//                        return true;
//                    }
//                }
//            }
//            return false;
//        }

//        /// <summary>
//        /// Send PTZ move command
//        /// </summary>
//        public async Task<bool> MoveAsync(string direction, int speed = 1)
//        {
//            if (string.IsNullOrEmpty(_sessionCookie))
//                throw new InvalidOperationException("Not logged in. Call LoginAsync first.");

//            var ptzPayload = new
//            {
//                method = "ptzMove",
//                @params = new
//                {
//                    direction = direction, // "left", "right", "up", "down", "stop"
//                    speed = speed
//                }
//            };

//            var content = new StringContent(JsonConvert.SerializeObject(ptzPayload), Encoding.UTF8, "application/json");
//            var response = await _httpClient.PostAsync($"http://{_cameraIp}/", content);

//            return response.IsSuccessStatusCode;
//        }

//        public void Dispose()
//        {
//            _httpClient?.Dispose();
//        }
////    }
////}
//}
