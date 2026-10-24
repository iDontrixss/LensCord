using DirectN;
using DirectN.Extensions;
using DirectN.Extensions.Com;

namespace Lenscord.VirtualCamera.Control;

internal static class Program
{
    private const string SourceClsid = "c5b1f0b4-6f4d-4c9b-9a3e-2a7d8e4f1b62";
    private const string FriendlyName = "Lenscord";

    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "create";
        if (command is not ("create" or "remove"))
        {
            Console.Error.WriteLine("Usage: Lenscord.VirtualCamera.Control <create|remove>");
            return 2;
        }

        try
        {
            Functions.MFStartup(Constants.MF_VERSION, 0).ThrowOnError();
            try
            {
                var result = Functions.MFCreateVirtualCamera(
                    MFVirtualCameraType.MFVirtualCameraType_SoftwareCameraSource,
                    MFVirtualCameraLifetime.MFVirtualCameraLifetime_System,
                    MFVirtualCameraAccess.MFVirtualCameraAccess_CurrentUser,
                    PWSTR.From(FriendlyName),
                    PWSTR.From($"{{{SourceClsid}}}"),
                    0,
                    0,
                    out var camera);
                result.ThrowOnError();

                using var virtualCamera = new ComObject<IMFVirtualCamera>(camera);
                if (command == "create")
                {
                    virtualCamera.Object.Start(null).ThrowOnError();
                    Console.WriteLine("Lenscord virtual camera created.");
                }
                else
                {
                    virtualCamera.Object.Remove().ThrowOnError();
                    Console.WriteLine("Lenscord virtual camera removed.");
                }
            }
            finally
            {
                Functions.MFShutdown().ThrowOnError();
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

