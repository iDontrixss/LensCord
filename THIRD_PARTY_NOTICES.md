# Componentes de terceros

| Componente | Uso | Licencia |
|---|---|---|
| [OBS Studio](https://github.com/obsproject/obs-studio) | La camara DirectShow de Windows (`virtualcam/DShow`) se compila a partir del `OutputFilter` de `win-dshow`, con los cambios de `virtualcam/DShow/obs-lenscord.patch` | GPL-2.0-or-later |
| [VCamNetSample](https://github.com/smourier/VCamNetSample) (Simon Mourier) | Base de la fuente de Media Foundation (`virtualcam/Source`, `virtualcam/Control`) | MIT |
| [DirectN](https://github.com/smourier/DirectN) | Interop COM de la fuente de Media Foundation (no se versiona; ver `virtualcam/README.md`) | MIT |
| [Avalonia UI](https://avaloniaui.net) y Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | Interfaz de la app | MIT / OFL (Inter) |
| [Svg.Controls.Skia.Avalonia](https://github.com/wieslawsoltes/Svg.Skia) y SkiaSharp | Iconos SVG | MIT |
| [Android platform-tools (adb)](https://developer.android.com/tools/releases/platform-tools) | Comunicacion con el telefono; se distribuye en el instalador de Windows | Apache-2.0 |
| [FFmpeg](https://ffmpeg.org) | Decodificacion H.264. El instalador de Windows descarga el build "essentials" de [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) y verifica su SHA256 | LGPL-2.1+ / GPL-2+ segun el build |

## Codigo fuente correspondiente (GPL)

La DLL `Lenscord.VirtualCamera.DShow.dll` es una obra derivada de OBS Studio. Se reproduce asi:

1. Clonar `https://github.com/obsproject/obs-studio` en `third_party/obs-studio` y hacer checkout de `72e86655715d9a25d69ce446aa4dd44ba58e10d2`.
2. Aplicar `git apply virtualcam/DShow/obs-lenscord.patch`.
3. Ejecutar `virtualcam/DShow/build-dshow.ps1`.

Quien reciba el instalador puede pedir ese codigo en este repositorio.
