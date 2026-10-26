# LensCord

Lenscord is an open-source app that turns your phone into a webcam, ready for use in calls via any application without needing to install anything on your phone. It is easy to use and features an intuitive interface.

Lenscord convierte un telefono Android en una webcam para el PC. No se instala nada en el telefono: la PC sube un modulo pequeno (`lenscord.dex`, unos 45 KB) por `adb`, lo ejecuta con `app_process` mientras dura la sesion y, al detener, deja el telefono como estaba.

## Plataformas

| Funcion | Windows | Linux | macOS |
|---|---|---|---|
| App (Avalonia / .NET 9) | si | si | si (sin probar) |
| Video por USB y Wi-Fi | si | si | si (sin probar) |
| Camara virtual para Discord, OBS, Meet... | si (DirectShow + Media Foundation) | si, con `v4l2loopback` | no disponible |

En macOS la camara virtual exige una extension de sistema firmada; hasta entonces se puede capturar la ventana de Lenscord desde OBS.

## Requisitos

- Telefono Android con **Depuracion USB** activada (Ajustes > Opciones de desarrollador).
- `adb` (platform-tools de Android) y `ffmpeg`. La app los busca en `tools/` del repo, junto al ejecutable y en el `PATH` (en macOS y Linux tambien en `/opt/homebrew/bin` y `/usr/local/bin`).
- .NET 9 SDK para compilar la app. JDK 11+ y el SDK de Android portable en `tools/android-sdk` (`platforms/android-35`, `build-tools/34.0.0`) para compilar el modulo del telefono.

## Compilar y ejecutar

```
# modulo del telefono (genera dispositivo/out/lenscord.dex)
Windows:      dispositivo\build-dex.ps1
Linux/macOS:  dispositivo/build-dex.sh

# app
dotnet run --project pc

# pruebas
dotnet test pc.Tests
```

### Linux

```
sudo apt install adb ffmpeg                # o el gestor de tu distribucion
sudo modprobe v4l2loopback devices=1 video_nr=10 card_label="Lenscord" exclusive_caps=1
```

El segundo comando crea la webcam virtual que veran Chrome, Zoom, OBS y Discord. Si `adb` no ve el telefono, revisa las reglas udev de Android.

### macOS

```
brew install ffmpeg android-platform-tools
```

## Estructura

| Carpeta | Contenido |
|---|---|
| `pc/` | App de escritorio (UI, sesion adb, decodificacion, camara virtual) |
| `pc.Tests/` | Pruebas unitarias |
| `dispositivo/` | Modulo Java que corre en el telefono y su script de build |
| `virtualcam/` | Camara virtual de Windows (Media Foundation, DirectShow, instaladores) |
| `release/` | Scripts del instalador de Windows (los binarios no se versionan) |
| `third_party/` | Fork local de OBS Studio (no se versiona; ver `virtualcam/README.md`) |

## Seguridad

- El video y el control viajan por un tunel de `adb` (USB) o por la red local (Wi-Fi directo). Cada conexion se autentica con un desafio HMAC-SHA256 y el secreto no viaja por la red, pero **el video por Wi-Fi no va cifrado**: usalo solo en redes de confianza.
- Mientras dura la sesion se pausa el desbloqueo facial de Samsung y se mantiene la pantalla encendida. Los valores originales se guardan en disco y se restauran aunque la app se cierre de golpe.
- `adb tcpip` deja el telefono accesible por la red: usa `Volver a USB` al terminar.

## Licencia

GPL-3.0 (ver `LICENSE`). Componentes de terceros en `THIRD_PARTY_NOTICES.md`.
