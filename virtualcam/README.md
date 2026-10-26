# Camara virtual

Pipeline: la app (`pc/Core/VirtualCameraPublisher.cs`) escribe frames NV12 de 1280x720 en `%ProgramData%\Lenscord\frames.nv12` y los lectores los entregan al sistema.

| Pieza | Ruta | Para que |
|---|---|---|
| Fuente Media Foundation | `Source/` | Camara de sistema de Windows 11 (`MFCreateVirtualCamera`) |
| Control | `Control/` | `create` / `remove` de la camara de sistema |
| Filtro DirectShow (OBS) | `DShow/build-dshow.ps1` + `DShow/obs-lenscord.patch` | La que ven Discord, OBS, Meet |
| Filtro DirectShow propio | `DShow/LenscordDShow.cpp` | Respaldo historico |
| Instaladores | `install-vcam.ps1`, `uninstall-vcam.ps1` | Registro COM, permisos y servicios |

## Formato de `frames.nv12`

Cabecera de 4096 bytes seguida de dos frames NV12 (1.382.400 bytes cada uno):

| Offset | Tipo | Campo |
|---|---|---|
| 0 | uint32 | Magia `0x46534E4C` ("LNSF"); 0 cuando la app se detiene |
| 4 | int32 | Version (1) |
| 8 / 12 | int32 | Ancho / alto |
| 16 | int32 | Tamano de frame |
| 20 | int32 | Slot vigente (0 o 1) |
| 24 | int64 | Secuencia; sube con cada frame y cada ~500 ms como latido |

Los lectores validan magia, dimensiones y longitud del archivo antes de leer.

## Fork de OBS

`third_party/obs-studio` no se versiona. Sus cambios viven en `DShow/obs-lenscord.patch` (base `72e86655715d9a25d69ce446aa4dd44ba58e10d2`):

```
git clone https://github.com/obsproject/obs-studio third_party/obs-studio
git -C third_party/obs-studio checkout 72e86655715d9a25d69ce446aa4dd44ba58e10d2
git -C third_party/obs-studio apply ../../virtualcam/DShow/obs-lenscord.patch
```

## DirectN

`Source/External/` (ignorado por su tamano) debe contener `DirectN.dll` y `DirectN.MediaFoundation.dll` de [DirectN](https://github.com/smourier/DirectN).

## Linux

No usa estos componentes. La app envia los frames a un dispositivo `v4l2loopback` mediante ffmpeg (ver el README principal).

## Uso del instalador de desarrollo

```
powershell -ExecutionPolicy Bypass -File install-vcam.ps1 -PayloadDirectory payload   # como administrador
```

Cierra Discord antes: mantiene cargada la DLL de DirectShow. `FrameServer` carga la DLL apenas se registra, asi que el script lo detiene mientras copia.
