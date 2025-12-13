using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace USV_Controller_V1._1
{
    /// <summary>
    /// Tapo Camera PTZ Controller - Using ONVIF ContinuousMove
    /// Based on actual Wireshark packet capture from AgentDVR
    /// </summary>
    public class TapoPtzController
    {
        private readonly string _cameraIp;
        private readonly int _cameraPort;
        private readonly string _username;
        private readonly string _password;
        private readonly HttpClient _httpClient;

        public TapoPtzController(string cameraIp, int cameraPort, string username, string password)
        {
            _cameraIp = cameraIp;
            _cameraPort = cameraPort;
            _username = username;
            _password = password;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        /// <summary>
        /// Generate ONVIF Password Digest for authentication
        /// </summary>
        private (string passwordDigest, string nonce, string created) GeneratePasswordDigest()
        {
            // Generate random nonce
            byte[] nonceBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(nonceBytes);
            }
            string nonceBase64 = Convert.ToBase64String(nonceBytes);

            // Get current UTC time in ISO 8601 format
            string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            // Calculate digest: Base64( SHA1( nonce + created + password ) )
            byte[] createdBytes = Encoding.UTF8.GetBytes(created);
            byte[] passwordBytes = Encoding.UTF8.GetBytes(_password);

            byte[] combined = new byte[nonceBytes.Length + createdBytes.Length + passwordBytes.Length];
            Buffer.BlockCopy(nonceBytes, 0, combined, 0, nonceBytes.Length);
            Buffer.BlockCopy(createdBytes, 0, combined, nonceBytes.Length, createdBytes.Length);
            Buffer.BlockCopy(passwordBytes, 0, combined, nonceBytes.Length + createdBytes.Length, passwordBytes.Length);

            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(combined);
                string passwordDigest = Convert.ToBase64String(hash);
                return (passwordDigest, nonceBase64, created);
            }
        }

        /// <summary>
        /// Move camera using ONVIF ContinuousMove (for smooth joystick control)
        /// </summary>
        public async Task<bool> ContinuousMove(double panVelocity, double tiltVelocity)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"📹 ContinuousMove: Pan={panVelocity:F2}, Tilt={tiltVelocity:F2}");

                // Generate authentication
                var (passwordDigest, nonce, created) = GeneratePasswordDigest();

                // ONVIF uses -1 to 1 range for velocity
                double panSpeed = Math.Clamp(panVelocity, -1.0, 1.0);
                double tiltSpeed = Math.Clamp(tiltVelocity, -1.0, 1.0);

                // Try both profile tokens that Tapo cameras use
                var profileTokens = new[] { "profile_2", "profile_1", "000" };

                foreach (var profileToken in profileTokens)
                {
                    // Build ONVIF SOAP envelope with ContinuousMove
                    string soapEnvelope = $@"<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://www.w3.org/2005/08/addressing"">
  <s:Header>
    <Security s:mustUnderstand=""0"" xmlns=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"">
      <UsernameToken>
        <Username>{_username}</Username>
        <Password Type=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"">{passwordDigest}</Password>
        <Nonce EncodingType=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"">{nonce}</Nonce>
        <Created xmlns=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"">{created}</Created>
      </UsernameToken>
    </Security>
    <a:Action s:mustUnderstand=""1"">http://www.onvif.org/ver20/ptz/wsdl/ContinuousMove</a:Action>
    <a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID>
    <a:ReplyTo><a:Address>http://www.w3.org/2005/08/addressing/anonymous</a:Address></a:ReplyTo>
    <a:To s:mustUnderstand=""1"">http://{_cameraIp}:{_cameraPort}/onvif/service</a:To>
  </s:Header>
  <s:Body xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
    <ContinuousMove xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>{profileToken}</ProfileToken>
      <Velocity>
        <PanTilt x=""{panSpeed:F2}"" y=""{tiltSpeed:F2}"" space=""http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace"" xmlns=""http://www.onvif.org/ver10/schema""/>
      </Velocity>
    </ContinuousMove>
  </s:Body>
</s:Envelope>";

                    var content = new StringContent(soapEnvelope, Encoding.UTF8, "application/soap+xml");
                    content.Headers.Add("action", "\"http://www.onvif.org/ver20/ptz/wsdl/ContinuousMove\"");

                    string url = $"http://{_cameraIp}:{_cameraPort}/onvif/service";

                    System.Diagnostics.Debug.WriteLine($"📤 POST {url} with profile: {profileToken}");

                    var response = await _httpClient.PostAsync(url, content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode && !responseText.Contains("Fault"))
                    {
                        System.Diagnostics.Debug.WriteLine($"✅ PTZ Success with {profileToken}!");
                        return true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠️ Failed with {profileToken}: {response.StatusCode}");
                    }
                }

                System.Diagnostics.Debug.WriteLine("❌ All profile tokens failed");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ PTZ exception: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> Stop()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"📹 Stop command");

                var (passwordDigest, nonce, created) = GeneratePasswordDigest();

                var profileTokens = new[] { "profile_2", "profile_1", "000" };

                foreach (var profileToken in profileTokens)
                {
                    string soapEnvelope = $@"<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://www.w3.org/2005/08/addressing"">
  <s:Header>
    <Security s:mustUnderstand=""0"" xmlns=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"">
      <UsernameToken>
        <Username>{_username}</Username>
        <Password Type=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"">{passwordDigest}</Password>
        <Nonce EncodingType=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"">{nonce}</Nonce>
        <Created xmlns=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"">{created}</Created>
      </UsernameToken>
    </Security>
    <a:Action s:mustUnderstand=""1"">http://www.onvif.org/ver20/ptz/wsdl/Stop</a:Action>
    <a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID>
    <a:ReplyTo><a:Address>http://www.w3.org/2005/08/addressing/anonymous</a:Address></a:ReplyTo>
    <a:To s:mustUnderstand=""1"">http://{_cameraIp}:{_cameraPort}/onvif/service</a:To>
  </s:Header>
  <s:Body xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
    <Stop xmlns=""http://www.onvif.org/ver20/ptz/wsdl"">
      <ProfileToken>{profileToken}</ProfileToken>
      <PanTilt>true</PanTilt>
      <Zoom>false</Zoom>
    </Stop>
  </s:Body>
</s:Envelope>";

                    var content = new StringContent(soapEnvelope, Encoding.UTF8, "application/soap+xml");
                    content.Headers.Add("action", "\"http://www.onvif.org/ver20/ptz/wsdl/Stop\"");

                    string url = $"http://{_cameraIp}:{_cameraPort}/onvif/service";

                    var response = await _httpClient.PostAsync(url, content);

                    if (response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"✅ Stop Success with {profileToken}!");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Stop exception: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}