using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace USV_Controller_V1._1
{
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

        private (string passwordDigest, string nonce, string created) GeneratePasswordDigest()
        {
            byte[] nonceBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(nonceBytes);
            }
            string nonceBase64 = Convert.ToBase64String(nonceBytes);

            string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

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

        public async Task<bool> ContinuousMove(double panVelocity, double tiltVelocity, double zoomVelocity = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"📹 ContinuousMove: Pan={panVelocity:F2}, Tilt={tiltVelocity:F2}, Zoom={zoomVelocity:F2}");

                var (passwordDigest, nonce, created) = GeneratePasswordDigest();

                double panSpeed = Math.Clamp(panVelocity, -1.0, 1.0);
                double tiltSpeed = Math.Clamp(tiltVelocity, -1.0, 1.0);
                double zoomSpeed = Math.Clamp(zoomVelocity, -1.0, 1.0);

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
        <Zoom x=""{zoomSpeed:F2}"" space=""http://www.onvif.org/ver10/tptz/ZoomSpaces/VelocityGenericSpace"" xmlns=""http://www.onvif.org/ver10/schema""/>
      </Velocity>
    </ContinuousMove>
  </s:Body>
</s:Envelope>";

                    // ✅ FIX: Set Content-Type correctly with action in header
                    var content = new StringContent(soapEnvelope, Encoding.UTF8);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml")
                    {
                        CharSet = "utf-8"
                    };
                    content.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("action", "\"http://www.onvif.org/ver20/ptz/wsdl/ContinuousMove\""));

                    string url = $"http://{_cameraIp}:{_cameraPort}/onvif/service";

                    var response = await _httpClient.PostAsync(url, content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode && !responseText.Contains("Fault"))
                    {
                        System.Diagnostics.Debug.WriteLine($"✅ PTZ ContinuousMove Success with {profileToken}!");
                        return true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠️ ContinuousMove failed with {profileToken}: {response.StatusCode}");
                        System.Diagnostics.Debug.WriteLine($"Response: {responseText}");
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
                    // ✅ FIX: Set Zoom to TRUE like in Wireshark capture
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
      <Zoom>true</Zoom>
    </Stop>
  </s:Body>
</s:Envelope>";

                    // ✅ FIX: Set Content-Type correctly with action in header
                    var content = new StringContent(soapEnvelope, Encoding.UTF8);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml")
                    {
                        CharSet = "utf-8"
                    };
                    content.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("action", "\"http://www.onvif.org/ver20/ptz/wsdl/Stop\""));

                    string url = $"http://{_cameraIp}:{_cameraPort}/onvif/service";

                    var response = await _httpClient.PostAsync(url, content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode && !responseText.Contains("Fault"))
                    {
                        System.Diagnostics.Debug.WriteLine($"✅ Stop Success with {profileToken}!");
                        return true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠️ Stop failed with {profileToken}: {response.StatusCode}");
                        System.Diagnostics.Debug.WriteLine($"Response: {responseText}");
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