# Lenscord - PROGRESO

Proyecto: `C:\Lenscord` (Windows; codigo preparado para Linux/macOS)
Actualizado: 2026-10-23

## Estado actual

La captura real funciona por **USB** y por **Wi-Fi**, sin instalar nada en el
telefono. El video H.264 se ve en la app, SmartFace se pausa mientras dura la
sesion y la limpieza deja el telefono sin rastros.

La app ya no esta bloqueada por la atribucion de paquete de Android 16.

## Restriccion del proyecto

**Nada se instala en el celular.** Se usa adb portable, un `.dex` subido a
`/data/local/tmp` y ejecutado con `app_process`; no hay APK ni cambio de apps
del sistema.

## Arquitectura

| Pieza | Ruta |
|---|---|
| UI y sesion PC | `pc/` (.NET 9 + Avalonia 11) |
| Payload del celular | `dispositivo/src/com/lenscord/LenscordDevice.java` |
| Dex compilado | `dispositivo/out/lenscord.dex` |
| Build del dex | `dispositivo/build-dex.ps1` |
| adb portable | `tools/android-sdk/platform-tools/adb.exe` |
| Evidencia y logs | `evidencia/app.log`, `evidencia/stream.h264` |
| Webcam virtual (MF) | `virtualcam/Source/`, `virtualcam/payload/` |
| Webcam virtual (DShow) | `virtualcam/DShow/` (filtro DirectShow, la que ve Discord) |

Flujo: la PC sube el dex, abre forwards adb de video/control, lanza
`app_process`, espera `escuchando`, conecta sockets y decodifica H.264 con
ffmpeg. Al detener envia `{"cmd":"stop"}`, mata el payload, restaura el telefono
y borra el dex.

## Verificado con el dispositivo real

- Samsung SM-A156M, Android 16/API 36, conexion USB (serial omitido).
- Camaras detectadas: 4; camara 0 trasera; formato real 1280x720@30.
- Video USB estable mas de 2 minutos.
- Video Wi-Fi estable mas de 60 segundos; forwards confirmados sobre
  `192.168.1.5:5555`.
- UI real: `Transmitiendo` y `Tu camara esta lista`.
- SPS/PPS se envian una sola vez; el buffer `BUFFER_FLAG_CODEC_CONFIG` ya no se
  duplica ni cuenta como frame.
- Build PC: 0 errores, 0 advertencias. Build dex: 40.5 KB, 11 clases.
- Al detener: appops vuelve a `CAMERA: foreground`, la pantalla recupera sus
  valores, no quedan forwards, procesos ni `/data/local/tmp/lenscord.dex`.

## Wi-Fi

- El panel Wi-Fi permite tres caminos:
  1. Emparejamiento por depuracion inalambrica (IP, puerto y codigo).
  2. `Conectar` a una IP/puerto ya conocidos.
  3. `Activar por USB`: lee la IP del celular, ejecuta `adb tcpip 5555`,
     conecta `IP:5555`, comprueba shell y cambia a modo Wi-Fi.
- En modo Wi-Fi se prefiere el transporte `IP:5555`, aunque el USB siga
  conectado.
- `Volver a USB` ejecuta `adb usb`, desconecta el transporte Wi-Fi y vuelve a
  preferir USB.
- Si el transporte Wi-Fi cae, la restauracion de SmartFace y la limpieza
  reintentan por el transporte USB disponible.
- Estado final de la ultima prueba: adbd de vuelta en modo USB.

## Pantalla y desbloqueo facial

- SmartFace se pausa con `cmd appops set --uid 1000 CAMERA ignore` y se restaura
  con `allow` (el sistema reporta `foreground`).
- No se usa `am freeze`: no persiste y solo afecta al PID actual.
- `default`/`reset` no limpian el UID mode; para restaurar se usa `allow`.
- Al iniciar se guardan `stay_on_while_plugged_in` y `screen_off_timeout`; se
  ponen en 7 y maximo para que la camara no se apague al dormir el telefono.
  Al detener se restauran.
- Si el usuario apaga la pantalla manualmente, Samsung desconecta la camara;
  la app lo detecta y detiene la vista. No se puede evitar sin impedir el boton
  de apagado.

## Webcam virtual DirectShow

Discord devuelve error 2014 con el filtro propio. El ultimo log muestra que
Discord pide I420 640x480 y rechaza cada `ReceiveConnection` con
`0x8004022A` (`VFW_E_TYPE_NOT_ACCEPTED`), antes de recibir frames.

El DShow propio se conserva como respaldo en `virtualcam/DShow/`, pero
`build-dshow.ps1` ahora compila el `OutputFilter` DirectShow de OBS adaptado
para Lenscord. El fork local esta en `third_party/obs-studio` (rama
`lenscord-virtualcam`) y su lector abre `%ProgramData%\Lenscord\frames.nv12`
con el mismo protocolo que publica `pc/Core/VirtualCameraPublisher.cs`.

- Identidad del dispositivo: CLSID `{5B1F6468-8F22-4A63-B38C-27357AE6D492}`;
  nombre `Lenscord Virtual Camera`.
- Negociacion/formats y entrega de muestras usan el `OutputFilter` de OBS;
  conversion de frames y temporizado siguen el codigo upstream adaptado.
- Build x64 `RelWithDebInfo` compilado; el DLL nuevo esta en
  `virtualcam/payload/Lenscord.VirtualCamera.DShow.dll`.
- El DLL activo bajo `C:\Program Files\Lenscord\VirtualCamera\` sigue siendo
  el anterior: Discord (PID 5620 en el log DShow) lo mantiene cargado y el
  reemplazo falla por archivo en uso. El instalador ahora precomprueba el lock
  y explica que hay que cerrar Discord antes de reintentar.
- Falta registrar el DLL nuevo y verificar que Discord deje de mostrar error
  2014 y reciba video real.

### Verificado en esta PC (Windows 11, x64)

- Pruebas enumeradas abajo corresponden al filtro propio anterior, no al fork
  OBS; el reemplazo OBS solo tiene compilacion x64 confirmada por ahora.
- Prueba de factory: 14/14 (LoadLibrary, 4 exports, IBaseFilter, EnumPins,
  IAMStreamConfig con 12 formatos y caps de 128 bytes, DllCanUnloadNow OK).
- `regsvr32` exit 0; la categoria enumera `Lenscord` junto a LSVCam/OBS/etc.
- Prueba de grafo: 13/13 (enumeracion como camara, BindToObject, AddFilter,
  `ConnectDirect` camara->sink propio con negociacion I420 1280x720,
  `IMediaControl::Run`, 45 muestras en 1.5 s a 32 ms/iteracion = 30 fps,
  longitud 1382400, Y en 16..235, contenido EN MOVIMIENTO, exit 0 limpio).
- El sink del test es propio (TsFiltro) porque el `SampleGrabber` (qedit)
  devuelve `E_NOTIMPL` en `GetCurrentBuffer` en Windows moderno.

### Avisos de operacion

- Los servicios `FrameServer`/`FrameServerMonitor` cargan la DLL apenas se
  registra: para reemplazarla, renombrar la DLL en uso a `.old` (Windows
  permite renombrar en el mismo volumen), copiar la nueva y borrar `.old`.
- Si el publicador del telefono esta apagado o en standby, la camara entrega
  standby plana (Y=16 + banda 128): Discord la ve en negro, no es un fallo.

## Riesgos y limites conocidos

- AppOps UID 1000 es un cambio temporal y amplio (UID del sistema compartido).
  Un crash o force-kill puede dejar `CAMERA: ignore`; el siguiente inicio lo
  detecta y lo limpia. La detencion normal lo restaura y reintenta por USB.
- Un crash tambien puede dejar la pantalla con timeout maximo; la detencion
  normal lo restaura.
- `sizes=[]` y `hwLevel=0` siguen sin explicacion; el fallback 720p funciona.
- Zoom y selector de lente aun tienen deuda de Fase 4: el zoom de la UI no esta
  sincronizado con `gZoomFrac`, el cambio de lente no siempre aplica y
  `StreamEncoder.release()` no reinicia `alive`.
- Faltan latches de un solo uso (`gAbierto`, `gConfigOk`, `gSesionOk`,
  `gAperturaOk`) y resolucion dinamica.
- Webcam virtual: filtro DirectShow verificado (30 fps, contenido real del
  telefono); falta la prueba end-to-end eligiendola en Discord.
- Linux/macOS: no comprobados desde esta PC.
- ADB por Wi-Fi deja el telefono accesible en la red; usar `Volver a USB` al
  terminar.

## Proximos pasos

1. Cerrar Fase 4 del payload: latches, `release()` que reinicie `alive`, zoom y
   selector de lente.
2. Probar la camara `Lenscord` en Discord (Discord solo enumera DirectShow en
   Windows): elegirla en Configuracion -> Video con la sesion del telefono
   activa y confirmar frames reales a ~30 fps.
3. Instalar end-to-end con `install-vcam.ps1` y repetir la prueba en Discord
   con la DLL desde Program Files.
4. Probar publicacion en Linux/macOS.
5. Repetir una sesion larga por Wi-Fi con la pantalla encendida y medir latencia
   frente a USB.

## Como probar

1. Ejecutar `pc\bin\Release\net9.0\Lenscord.exe`.
2. USB: conectar el celular con depuracion USB y pulsar `Iniciar camara`.
3. Wi-Fi: pulsar el selector `USB` de la barra superior, entrar al panel Wi-Fi y
   pulsar `Activar por USB`; luego `Iniciar camara`.
4. Al terminar, `Detener` y, si se uso Wi-Fi, `Volver a USB`.

### Webcam en Discord

1. Con la DLL ya registrada (`regsvr32` o `install-vcam.ps1`) salir y volver a
   abrir Discord.
2. Iniciar la camara del telefono en la app de Lenscord (sin la sesion activa
   la camara se ve en negro/standby).
3. Configuracion de Discord -> Video -> Camara: elegir `Lenscord`.
4. La vista debe mostrar el video del telefono a ~30 fps.
