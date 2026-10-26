using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lenscord.Core;

public sealed record Chequeo(string Titulo, bool Ok, string Detalle);

public static class Entorno
{
    private const string ClsidDShow = "{5B1F6468-8F22-4A63-B38C-27357AE6D492}";

    public static List<Chequeo> Comprobar(Adb adb)
    {
        var lista = new List<Chequeo>();

        lista.Add(adb.Available
            ? new Chequeo(Loc.T("onb.chk.adb"), true, adb.AdbPath)
            : new Chequeo(Loc.T("onb.chk.adb"), false, Loc.T("onb.chk.adb_falta")));

        lista.Add(Rutas.BuscarDex() is { } dex
            ? new Chequeo(Loc.T("onb.chk.dex"), true, dex)
            : new Chequeo(Loc.T("onb.chk.dex"), false, Loc.T("onb.chk.dex_falta", Rutas.ComandoBuildDex)));

        lista.Add(FfmpegH264Decoder.BuscarFfmpeg(Rutas.Raiz) is { } ffmpeg
            ? new Chequeo(Loc.T("onb.chk.ffmpeg"), true, ffmpeg)
            : new Chequeo(Loc.T("onb.chk.ffmpeg"), false, Loc.T("onb.chk.ffmpeg_falta")));

        lista.Add(CamaraVirtual());
        return lista;
    }

    private static Chequeo CamaraVirtual()
    {
        string titulo = Loc.T("onb.chk.camara_virtual");
        if (OperatingSystem.IsWindows())
            return DShowRegistrado()
                ? new Chequeo(titulo, true, Loc.T("onb.chk.camara_virtual_ok"))
                : new Chequeo(titulo, false, Loc.T("onb.chk.camara_virtual_windows"));
        if (OperatingSystem.IsLinux())
            return HayDispositivoV4l2()
                ? new Chequeo(titulo, true, Loc.T("onb.chk.camara_virtual_ok"))
                : new Chequeo(titulo, false, Loc.T("onb.chk.camara_virtual_linux"));
        return new Chequeo(titulo, false, Loc.T("onb.chk.camara_virtual_mac"));
    }

    [SupportedOSPlatform("windows")]
    private static bool ClaveExiste(RegistryHive hive, RegistryView vista)
    {
        try
        {
            using var raiz = RegistryKey.OpenBaseKey(hive, vista);
            using var clave = raiz.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + ClsidDShow);
            return clave is not null;
        }
        catch { return false; }
    }

    private static bool DShowRegistrado()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return ClaveExiste(RegistryHive.LocalMachine, RegistryView.Registry64)
            || ClaveExiste(RegistryHive.LocalMachine, RegistryView.Registry32)
            || ClaveExiste(RegistryHive.CurrentUser, RegistryView.Default);
    }

    private static bool HayDispositivoV4l2()
    {
        try
        {
            return Directory.Exists("/dev") && Directory.GetFiles("/dev", "video*").Length > 0;
        }
        catch { return false; }
    }
}
