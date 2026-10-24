namespace VCamNetSampleSourceAOT;

public static class Shared
{
    public const string CLSID_Lenscord = "c5b1f0b4-6f4d-4c9b-9a3e-2a7d8e4f1b62";
    public const int Width = 1280;
    public const int Height = 720;
    public const int Fps = 30;
    public const int FrameSize = Width * Height * 3 / 2;
    public const int Rgb32FrameSize = Width * Height * 4;
    public const int HeaderSize = 4096;
    public const uint FrameMagic = 0x46534E4C; // "LNSF" little-endian

    public static string FrameFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Lenscord",
        "frames.nv12");
}
