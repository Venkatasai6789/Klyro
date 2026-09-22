using System;
using System.Collections.Generic;
using Klyro.Core.Network;
using Xunit;

namespace Klyro.NetworkTests;

public class WifiNetworkManagerTests
{
    [Theory]
    [InlineData("KLU_WIFI", true)]
    [InlineData("klu_wifi", true)]
    [InlineData("KLU_STUDENT", true)]
    [InlineData("Kalasalingam_Campus", true)]
    [InlineData("8601A -  2", true)]
    [InlineData("8601B - 1", true)]
    [InlineData("8301A - 1", true)]
    [InlineData("8301B - 2", true)]
    [InlineData("8401 - A2", true)]
    [InlineData("Home-5G", false)]
    [InlineData("TP-Link_Guest", false)]
    [InlineData("Starbucks_WiFi", false)]
    [InlineData("iPhone 15", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsUniversityCampusNetwork_ClassifiesCorrectly(string ssid, bool expected)
    {
        bool result = WifiNetworkManager.IsUniversityCampusNetwork(ssid);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseNetworksOutput_ParsesMultipleNetworksAndExtractsBestSignal()
    {
        string sampleOutput = @"
Interface name : Wi-Fi 
There are 3 networks currently visible. 

SSID 1 : KLU_WIFI
    Network type            : Infrastructure
    Authentication          : Open
    Encryption              : None 
    BSSID 1                 : 3a:7b:e9:20:d0:84
         Signal             : 88%  
         Radio type         : 802.11ax
    BSSID 2                 : 3a:7b:e9:20:d0:85
         Signal             : 94%  
         Radio type         : 802.11ax

SSID 2 : 8601A -  2
    Network type            : Infrastructure
    Authentication          : Open
    Encryption              : None 
    BSSID 1                 : 12:34:56:78:9a:bc
         Signal             : 75%  

SSID 3 : Faculty_Secure
    Network type            : Infrastructure
    Authentication          : WPA2-Personal
    Encryption              : CCMP 
    BSSID 1                 : aa:bb:cc:dd:ee:ff
         Signal             : 60%  
";

        var networks = new Dictionary<string, WifiNetworkItem>(StringComparer.OrdinalIgnoreCase);
        var savedProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KLU_WIFI" };
        string activeSsid = "KLU_WIFI";

        WifiNetworkManager.ParseNetworksOutput(sampleOutput, networks, savedProfiles, activeSsid);

        Assert.Equal(3, networks.Count);

        // Verify KLU_WIFI
        Assert.True(networks.ContainsKey("KLU_WIFI"));
        var klu = networks["KLU_WIFI"];
        Assert.Equal(94, klu.SignalPercent); // Higher of 88% and 94%
        Assert.Equal("Open", klu.Authentication);
        Assert.Equal("None", klu.Encryption);
        Assert.True(klu.IsOpen);
        Assert.True(klu.IsConnected);
        Assert.True(klu.IsCampusNetwork);
        Assert.True(klu.HasProfile);
        Assert.Equal(2, klu.Bssids.Count);

        // Verify 8601A -  2
        Assert.True(networks.ContainsKey("8601A -  2"));
        var lab = networks["8601A -  2"];
        Assert.Equal(75, lab.SignalPercent);
        Assert.True(lab.IsOpen);
        Assert.False(lab.IsConnected);
        Assert.True(lab.IsCampusNetwork); // Recognized as campus lab AP
        Assert.False(lab.HasProfile);

        // Verify Faculty_Secure
        Assert.True(networks.ContainsKey("Faculty_Secure"));
        var secure = networks["Faculty_Secure"];
        Assert.Equal(60, secure.SignalPercent);
        Assert.False(secure.IsOpen);
        Assert.Equal("WPA2-Personal", secure.Authentication);
        Assert.False(secure.IsCampusNetwork);
    }

    [Fact]
    public void GenerateOpenProfileXml_GeneratesValidOpenProfile()
    {
        string ssid = "KLU_LAB_TEST";
        string xml = WifiNetworkManager.GenerateOpenProfileXml(ssid);

        Assert.Contains($"<name>{ssid}</name>", xml);
        Assert.Contains("<authentication>open</authentication>", xml);
        Assert.Contains("<encryption>none</encryption>", xml);
        Assert.Contains("<useOneX>false</useOneX>", xml);
    }

    [Fact]
    public void GenerateWpa2PskProfileXml_GeneratesValidWpa2Profile()
    {
        string ssid = "Campus_Secured";
        string password = "SecretPassword123";
        string xml = WifiNetworkManager.GenerateWpa2PskProfileXml(ssid, password);

        Assert.Contains($"<name>{ssid}</name>", xml);
        Assert.Contains("<authentication>WPA2PSK</authentication>", xml);
        Assert.Contains("<encryption>AES</encryption>", xml);
        Assert.Contains($"<keyMaterial>{password}</keyMaterial>", xml);
    }
}
