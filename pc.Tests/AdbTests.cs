using Lenscord.Core;
using Xunit;

namespace Lenscord.Tests;

public class AdbTests
{
    [Fact]
    public void ParsearDispositivos_IgnoraCabeceraYAvisosDelDemonio()
    {
        const string salida =
            "* daemon not running; starting now at tcp:5037\n" +
            "* daemon started successfully\n" +
            "List of devices attached\n" +
            "R58N123ABC             device usb:1-2 product:a15 model:SM_A156M device:a15 transport_id:3\n" +
            "192.168.1.5:5555       device product:a15 model:SM_A156M device:a15 transport_id:4\n";

        var lista = Adb.ParsearDispositivos(salida);

        Assert.Equal(2, lista.Count);
        Assert.Equal("R58N123ABC", lista[0].Serial);
        Assert.Equal("SM A156M", lista[0].Model);
        Assert.True(lista[0].Usb);
        Assert.False(lista[1].Usb);
    }

    [Fact]
    public void ParsearDispositivos_OfflineNoCuentaComoUsb()
    {
        var lista = Adb.ParsearDispositivos("R58N123ABC offline transport_id:1\n");

        Assert.Single(lista);
        Assert.False(lista[0].Usb);
    }

    [Theory]
    [InlineData("192.168.1.5:5555", true)]
    [InlineData("adb-R58N123ABC-xYz._adb-tls-connect._tcp", true)]
    [InlineData("R58N123ABC", false)]
    public void EsWifi_DetectaTransportes(string serial, bool esperado)
    {
        Assert.Equal(esperado, Adb.EsWifi(serial));
    }

    [Fact]
    public void ParsearMdns_ExtraeTipoYEndpoint()
    {
        const string salida =
            "List of discovered mdns services\n" +
            "adb-R58N123ABC-xYz\t_adb-tls-connect._tcp.\t192.168.1.5:41234\n" +
            "otro\t_http._tcp.\t192.168.1.9:80\n";

        var lista = Adb.ParsearMdns(salida);

        Assert.Single(lista);
        Assert.Equal("192.168.1.5:41234", lista[0].Endpoint);
    }
}
