using System;
using SetNet.Upnp;
using Xunit;

namespace SetNet.UnitTests;

/// <summary>Unit tests for the UPnP SSDP/SOAP parsing helpers (no real gateway required).</summary>
public class UpnpParsingTests
{
    [Fact]
    public void ParseLocation_Reads_Header_Case_Insensitively()
    {
        var response = "HTTP/1.1 200 OK\r\n" +
                       "CACHE-CONTROL: max-age=120\r\n" +
                       "Location: http://192.168.1.1:5000/rootDesc.xml\r\n" +
                       "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
        var location = UpnpXml.ParseLocation(response);
        Assert.NotNull(location);
        Assert.Equal("http://192.168.1.1:5000/rootDesc.xml", location!.ToString());
    }

    [Fact]
    public void ParseLocation_Returns_Null_Without_Header()
    {
        Assert.Null(UpnpXml.ParseLocation("HTTP/1.1 200 OK\r\nST: something\r\n\r\n"));
    }

    [Fact]
    public void FindService_Resolves_Relative_ControlUrl_Against_Location()
    {
        var xml = @"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"">
  <device>
    <deviceList><device>
      <serviceList>
        <service>
          <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
          <controlURL>/ctl/IPConn</controlURL>
        </service>
      </serviceList>
    </device></deviceList>
  </device>
</root>";
        var found = UpnpXml.FindService(xml, new Uri("http://192.168.1.1:5000/rootDesc.xml"));
        Assert.NotNull(found);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", found!.Value.ServiceType);
        Assert.Equal("http://192.168.1.1:5000/ctl/IPConn", found.Value.ControlUrl.ToString());
    }

    [Fact]
    public void FindService_Honours_UrlBase_And_Prefers_V2()
    {
        var xml = @"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"">
  <URLBase>http://10.0.0.1:49152/</URLBase>
  <device><serviceList>
    <service>
      <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
      <controlURL>v1control</controlURL>
    </service>
    <service>
      <serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType>
      <controlURL>v2control</controlURL>
    </service>
  </serviceList></device>
</root>";
        var found = UpnpXml.FindService(xml, new Uri("http://192.168.1.1:5000/rootDesc.xml"));
        Assert.NotNull(found);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:2", found!.Value.ServiceType);
        Assert.Equal("http://10.0.0.1:49152/v2control", found.Value.ControlUrl.ToString());
    }

    [Fact]
    public void FindService_Returns_Null_When_No_Wan_Service()
    {
        var xml = @"<root><device><serviceList><service>
                      <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                      <controlURL>/l3f</controlURL>
                    </service></serviceList></device></root>";
        Assert.Null(UpnpXml.FindService(xml, new Uri("http://192.168.1.1:5000/rootDesc.xml")));
        Assert.Null(UpnpXml.FindService("not xml at all", new Uri("http://192.168.1.1:5000/rootDesc.xml")));
    }

    [Fact]
    public void BuildSoapRequest_Escapes_Argument_Values()
    {
        var soap = UpnpXml.BuildSoapRequest(
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "AddPortMapping",
            new[] { ("NewPortMappingDescription", "Tom & Jerry <game>") });
        Assert.Contains("Tom &amp; Jerry &lt;game&gt;", soap);
        Assert.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", soap);
    }

    [Fact]
    public void ParseSoapValue_And_Fault_Roundtrip()
    {
        var ok = @"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><u:GetExternalIPAddressResponse xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
    <NewExternalIPAddress>203.0.113.7</NewExternalIPAddress>
  </u:GetExternalIPAddressResponse></s:Body>
</s:Envelope>";
        Assert.Equal("203.0.113.7", UpnpXml.ParseSoapValue(ok, "NewExternalIPAddress"));
        Assert.Null(UpnpXml.ParseSoapFault(ok));

        var fault = @"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body><s:Fault>
    <detail><UPnPError xmlns=""urn:schemas-upnp-org:control-1-0"">
      <errorCode>718</errorCode>
      <errorDescription>ConflictInMappingEntry</errorDescription>
    </UPnPError></detail>
  </s:Fault></s:Body>
</s:Envelope>";
        var parsed = UpnpXml.ParseSoapFault(fault);
        Assert.NotNull(parsed);
        Assert.Equal(718, parsed!.Value.Code);
        Assert.Equal("ConflictInMappingEntry", parsed.Value.Description);
    }
}
