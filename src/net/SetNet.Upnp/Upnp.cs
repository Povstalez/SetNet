using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SetNet.Config;
using SetNet.Core.Transport;

[assembly: InternalsVisibleTo("SetNet.UnitTests")]

namespace SetNet.Upnp
{
    /// <summary>The transport protocol of a UPnP port mapping.</summary>
    public enum UpnpProtocol
    {
        /// <summary>Map a TCP port.</summary>
        Tcp,

        /// <summary>Map a UDP port.</summary>
        Udp,
    }

    /// <summary>Thrown when UPnP discovery or a SOAP action fails (no gateway, gateway refused, malformed answer).</summary>
    public sealed class UpnpException : Exception
    {
        /// <summary>The UPnP error code returned by the gateway, or 0 when the failure wasn't a SOAP fault.</summary>
        public int ErrorCode { get; }

        /// <summary>Creates the exception with a message.</summary>
        public UpnpException(string message) : base(message) { }

        /// <summary>Creates the exception with a message and the gateway's UPnP error code.</summary>
        public UpnpException(string message, int errorCode) : base(message) { ErrorCode = errorCode; }
    }

    // ---- SSDP / SOAP parsing (kept side-effect free so it's unit-testable) ----

    internal static class UpnpXml
    {
        /// <summary>Service types we can drive, in preference order (IGDv2 first).</summary>
        public static readonly string[] ServiceTypes =
        {
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        };

        /// <summary>Extracts the LOCATION header from an SSDP HTTPU response; null when absent.</summary>
        public static Uri? ParseLocation(string ssdpResponse)
        {
            foreach (var raw in ssdpResponse.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                if (!line.Substring(0, idx).Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Substring(idx + 1).Trim();
                return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
            }
            return null;
        }

        /// <summary>
        /// Scans a device-description document for the first WAN(IP|PPP)Connection service and returns its
        /// service type + control URL resolved against <paramref name="location"/> (honouring URLBase).
        /// </summary>
        public static (string ServiceType, Uri ControlUrl)? FindService(string deviceDescriptionXml, Uri location)
        {
            XDocument doc;
            try { doc = XDocument.Parse(deviceDescriptionXml); }
            catch { return null; }

            // Old gateways may declare a URLBase that relative control URLs resolve against; default to the doc's origin.
            var baseUri = new Uri(location.GetLeftPart(UriPartial.Authority));
            var urlBase = FirstLocal(doc, "URLBase")?.Value;
            if (!string.IsNullOrWhiteSpace(urlBase) && Uri.TryCreate(urlBase, UriKind.Absolute, out var declared)) baseUri = declared;

            foreach (var wanted in ServiceTypes)
            {
                foreach (var service in doc.Descendants())
                {
                    if (service.Name.LocalName != "service") continue;
                    var type = ChildValue(service, "serviceType");
                    if (!string.Equals(type, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    var control = ChildValue(service, "controlURL");
                    if (string.IsNullOrWhiteSpace(control)) continue;
                    // Scheme check matters: on Unix "/ctl/IPConn" parses as an absolute file:// URI.
                    if (Uri.TryCreate(control, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                        return (wanted, abs);
                    if (Uri.TryCreate(baseUri, control, out var resolved)) return (wanted, resolved);
                }
            }
            return null;
        }

        /// <summary>Builds a SOAP envelope for one IGD action with the given (name, value) arguments.</summary>
        public static string BuildSoapRequest(string serviceType, string action, IEnumerable<(string Name, string Value)> args)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
            sb.Append("<s:Body>");
            sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(serviceType).Append("\">");
            foreach (var (name, value) in args)
                sb.Append('<').Append(name).Append('>').Append(EscapeXml(value)).Append("</").Append(name).Append('>');
            sb.Append("</u:").Append(action).Append('>');
            sb.Append("</s:Body></s:Envelope>");
            return sb.ToString();
        }

        /// <summary>Reads the text of the first element named <paramref name="elementName"/> anywhere in a SOAP response; null when absent.</summary>
        public static string? ParseSoapValue(string soapXml, string elementName)
        {
            try
            {
                var doc = XDocument.Parse(soapXml);
                return FirstLocal(doc, elementName)?.Value;
            }
            catch { return null; }
        }

        /// <summary>Extracts (code, description) from a UPnPError SOAP fault; null when the response isn't a fault.</summary>
        public static (int Code, string Description)? ParseSoapFault(string soapXml)
        {
            try
            {
                var doc = XDocument.Parse(soapXml);
                var error = FirstLocal(doc, "UPnPError");
                if (error == null) return null;
                var codeText = ChildValue(error, "errorCode");
                int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
                return (code, ChildValue(error, "errorDescription") ?? "");
            }
            catch { return null; }
        }

        private static XElement? FirstLocal(XDocument doc, string localName)
        {
            foreach (var el in doc.Descendants())
                if (el.Name.LocalName == localName) return el;
            return null;
        }

        private static string? ChildValue(XElement parent, string localName)
        {
            foreach (var el in parent.Elements())
                if (el.Name.LocalName == localName) return el.Value.Trim();
            return null;
        }

        private static string EscapeXml(string value)
            => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    /// <summary>
    /// A discovered UPnP Internet Gateway Device (your router). Drive it to read the external IP and to add or
    /// remove port mappings so peers on the internet can reach a server (or a punched host) behind this NAT.
    /// </summary>
    public sealed class UpnpDevice
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>The WAN service type the gateway exposed (WANIPConnection:1/:2 or WANPPPConnection:1).</summary>
        public string ServiceType { get; }

        /// <summary>The SOAP control endpoint for the WAN service.</summary>
        public Uri ControlUrl { get; }

        /// <summary>This machine's LAN address on the interface facing the gateway (used as the mapping's internal client).</summary>
        public IPAddress LocalAddress { get; }

        internal UpnpDevice(string serviceType, Uri controlUrl, IPAddress localAddress)
        {
            ServiceType = serviceType;
            ControlUrl = controlUrl;
            LocalAddress = localAddress;
        }

        /// <summary>Asks the gateway for its external (WAN) IP address.</summary>
        public async Task<IPAddress> GetExternalIpAsync(CancellationToken cancellationToken = default)
        {
            var response = await InvokeAsync("GetExternalIPAddress", Array.Empty<(string, string)>(), cancellationToken).ConfigureAwait(false);
            var text = UpnpXml.ParseSoapValue(response, "NewExternalIPAddress");
            if (!IPAddress.TryParse(text, out var address))
                throw new UpnpException("Gateway returned no parsable external IP address.");
            return address;
        }

        /// <summary>
        /// Maps <paramref name="externalPort"/> on the gateway to <paramref name="internalPort"/> on this machine.
        /// <paramref name="leaseSeconds"/> 0 means a permanent mapping (remove it with <see cref="DeletePortMappingAsync"/>
        /// on shutdown); some IGDv2 routers cap or refuse permanent leases, so pass an explicit lease when targeting those.
        /// </summary>
        public Task AddPortMappingAsync(UpnpProtocol protocol, int externalPort, int internalPort, string description, int leaseSeconds = 0, CancellationToken cancellationToken = default)
        {
            ValidatePort(externalPort, nameof(externalPort));
            ValidatePort(internalPort, nameof(internalPort));
            return InvokeAsync("AddPortMapping", new[]
            {
                ("NewRemoteHost", ""),
                ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", ProtocolName(protocol)),
                ("NewInternalPort", internalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewInternalClient", LocalAddress.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", description ?? "SetNet"),
                ("NewLeaseDuration", leaseSeconds.ToString(CultureInfo.InvariantCulture)),
            }, cancellationToken);
        }

        /// <summary>Removes a mapping previously created for <paramref name="externalPort"/>/<paramref name="protocol"/>.</summary>
        public Task DeletePortMappingAsync(UpnpProtocol protocol, int externalPort, CancellationToken cancellationToken = default)
        {
            ValidatePort(externalPort, nameof(externalPort));
            return InvokeAsync("DeletePortMapping", new[]
            {
                ("NewRemoteHost", ""),
                ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", ProtocolName(protocol)),
            }, cancellationToken);
        }

        private static void ValidatePort(int port, string name)
        {
            if (port < 1 || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(name, port, "Port must be 1..65535.");
        }

        private static string ProtocolName(UpnpProtocol protocol) => protocol == UpnpProtocol.Udp ? "UDP" : "TCP";

        /// <summary>Posts one SOAP action to the control URL and returns the raw response body (fault-checked).</summary>
        private async Task<string> InvokeAsync(string action, (string, string)[] args, CancellationToken cancellationToken)
        {
            var envelope = UpnpXml.BuildSoapRequest(ServiceType, action, args);
            using var request = new HttpRequestMessage(HttpMethod.Post, ControlUrl)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
            };
            // Quotes are required by the UPnP spec; some routers 500 without them.
            request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{ServiceType}#{action}\"");

            HttpResponseMessage response;
            try { response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new UpnpException($"UPnP action {action} failed: {ex.Message}"); }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var fault = UpnpXml.ParseSoapFault(body);
                if (fault != null) throw new UpnpException($"Gateway refused {action}: {fault.Value.Description} ({fault.Value.Code}).", fault.Value.Code);
                if (!response.IsSuccessStatusCode) throw new UpnpException($"UPnP action {action} failed: HTTP {(int)response.StatusCode}.");
                return body;
            }
        }
    }

    /// <summary>
    /// Discovers the LAN's UPnP Internet Gateway Device via SSDP and returns a <see cref="UpnpDevice"/> to manage
    /// port mappings — so a player-hosted server behind a home router becomes reachable without manual router setup.
    /// </summary>
    public static class UpnpPortMapper
    {
        private static readonly IPEndPoint SsdpEndPoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        /// <summary>
        /// Broadcasts an SSDP M-SEARCH and drives the first gateway that answers. Returns null when no gateway
        /// responded within <paramref name="timeoutMs"/> (no UPnP router, or UPnP disabled on it).
        /// </summary>
        public static async Task<UpnpDevice?> DiscoverAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            // Search for the IGD root device; gateways answering for v2 also expose the v1 services we scan for.
            foreach (var target in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2" })
            {
                var search = "M-SEARCH * HTTP/1.1\r\n" +
                             "HOST: 239.255.255.250:1900\r\n" +
                             "MAN: \"ssdp:discover\"\r\n" +
                             "MX: 2\r\n" +
                             $"ST: {target}\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(search);
                try { await socket.SendAsync(bytes, bytes.Length, SsdpEndPoint).ConfigureAwait(false); } catch { }
            }

            while (!cts.Token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    var receive = socket.ReceiveAsync();
                    using (cts.Token.Register(() => socket.Close()))
                        result = await receive.ConfigureAwait(false);
                }
                catch { break; }   // timeout closed the socket

                var location = UpnpXml.ParseLocation(Encoding.ASCII.GetString(result.Buffer));
                if (location == null) continue;

                var device = await FromLocationAsync(location, cancellationToken).ConfigureAwait(false);
                if (device != null) return device;
            }
            return null;
        }

        /// <summary>
        /// Builds a <see cref="UpnpDevice"/> from a known device-description URL (skipping SSDP). Useful when the
        /// gateway address is fixed, or in tests against a stubbed description endpoint.
        /// </summary>
        public static async Task<UpnpDevice?> FromLocationAsync(Uri location, CancellationToken cancellationToken = default)
        {
            if (location == null) throw new ArgumentNullException(nameof(location));

            string xml;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try { xml = await http.GetStringAsync(location).ConfigureAwait(false); }
            catch { return null; }

            var service = UpnpXml.FindService(xml, location);
            if (service == null) return null;

            var local = LocalAddressTowards(location);
            if (local == null) return null;

            return new UpnpDevice(service.Value.ServiceType, service.Value.ControlUrl, local);
        }

        /// <summary>The local address the OS routes through to reach <paramref name="gateway"/> — the mapping's internal client.</summary>
        private static IPAddress? LocalAddressTowards(Uri gateway)
        {
            try
            {
                if (!IPAddress.TryParse(gateway.Host, out var gatewayIp))
                {
                    var addrs = Dns.GetHostAddresses(gateway.Host);
                    if (addrs.Length == 0) return null;
                    gatewayIp = addrs[0];
                }
                using var probe = new Socket(gatewayIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(gatewayIp, gateway.IsDefaultPort ? 1900 : gateway.Port);   // no traffic — just picks the route
                return (probe.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch { return null; }
        }
    }

    /// <summary>Convenience glue between UPnP and a SetNet <see cref="Configuration"/>.</summary>
    public static class UpnpConfigurationExtensions
    {
        /// <summary>
        /// Discovers the gateway and maps every port this server configuration listens on: the TCP port for
        /// Tcp/Both transports and the UDP port for Udp/Both. Returns the device (keep it to unmap on shutdown),
        /// or null when no UPnP gateway answered.
        /// </summary>
        public static async Task<UpnpDevice?> MapServerPortsAsync(this Configuration config, string description = "SetNet server", int leaseSeconds = 0, CancellationToken cancellationToken = default)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var device = await UpnpPortMapper.DiscoverAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (device == null) return null;

            if (config.TransportType == TransportType.Tcp || config.TransportType == TransportType.Both)
                await device.AddPortMappingAsync(UpnpProtocol.Tcp, config.Port, config.Port, description, leaseSeconds, cancellationToken).ConfigureAwait(false);
            if (config.TransportType == TransportType.Udp || config.TransportType == TransportType.Both)
            {
                var udpPort = config.UdpPort > 0 ? config.UdpPort : config.Port;
                await device.AddPortMappingAsync(UpnpProtocol.Udp, udpPort, udpPort, description, leaseSeconds, cancellationToken).ConfigureAwait(false);
            }
            return device;
        }
    }
}
