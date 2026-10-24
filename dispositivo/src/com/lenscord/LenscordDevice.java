package com.lenscord;

import android.content.Context;
import android.graphics.Rect;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureFailure;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.CaptureResult;
import android.hardware.camera2.TotalCaptureResult;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.util.Range;
import android.util.Size;
import android.view.Surface;

import java.io.BufferedOutputStream;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.TreeSet;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Lenscord: carga util que corre DENTRO del telefono, sin instalar nada.
 *
 *   adb shell CLASSPATH=/data/local/tmp/lenscord.dex \
 *       app_process /data/local/tmp com.lenscord.LenscordDevice <puertoVideo> <puertoControl>
 *
 * El telefono ESCUCHA en 127.0.0.1 y la PC se conecta con "adb forward tcp:X tcp:Y".
 * Dos flujos:
 *   - video: elementary stream H.264 en Annex-B (start code 00 00 00 01)
 *   - control: una linea JSON por mensaje, en ambos sentidos
 */
public final class LenscordDevice {

    private static final String TAG = "Lenscord";
    private static final Charset UTF8 = Charset.forName("UTF-8");
    private static final byte[] START = new byte[]{0, 0, 0, 1};

    private static volatile boolean gRunning = true;
    private static boolean VERBOSO_METODOS = false;
    private static final Object gLock = new Object();

    private static CameraManager gCamMgr;
    private static Context gCtx;

    private static CameraDevice gDevice;
    private static CameraCaptureSession gSession;
    private static Surface gEncSurface;
    private static MediaCodec gCodec;
    private static StreamEncoder gEnc;
    private static HandlerThread gCamThread;
    private static Handler gCamHandler;

    private static String gCamId;
    private static float gZoomFrac = 1.0f;     // 0..1 = fraccion del maximo
    private static boolean gTorch = false;
    private static boolean gPaused = false;

    private static int gWidth = 1280;
    private static int gHeight = 720;
    private static int gFps = 30;
    private static int gBitrateKbps = 4096;

    private static OutputStream gCtrlOut;
    private static BufferedReader gCtrlIn;

    private LenscordDevice() { }

    // ================================================================ main
public static void main(String[] args) {
        try {
            // El hilo principal de app_process no tiene Looper; ActivityThread lo necesita.
            Acquire.prepararLooperPrincipal();
            if (args.length > 1 && args[0].equals("--dump")) { Volcar(args[1]); return; }
            if (args.length > 0 && args[0].equals("--probe")) { probe(); return; }
            if (args.length > 0 && args[0].equals("--open")) { abrirCamara(); return; }
            int vp = args.length > 0 ? Integer.parseInt(args[0]) : 38700;
            int cp = args.length > 1 ? Integer.parseInt(args[1]) : 38701;
            run(vp, cp);
        } catch (Throwable t) {
            log("FATAL " + t);
            t.printStackTrace();
            System.out.flush();
            System.exit(1);
        }
    }

// ================================================================ sondeo
    /** Vuelca miembros de una clase del framework tal como existen EN ESTE TELEFONO.
     *  Sirve para no inventar nombres de metodos o campos. */
    private static void Volcar(String clase) throws Exception {
        // "--dump clase:todos" lista ademas TODOS los metodos declarados.
        boolean todos = clase.indexOf(":todos") >= 0;
        if (todos) clase = clase.substring(0, clase.indexOf(":todos"));
        VERBOSO_METODOS = todos;
        Class<?> c = Class.forName(clase);
        System.out.println("== " + clase + " (campos/Metodos con 'ackage' o 'Uid' o 'Process') ==");
        java.lang.reflect.Field[] fs = c.getDeclaredFields();
        for (Field f : fs) {
            boolean esEstatico = Modifier.isStatic(f.getModifiers());
            String n = f.getName();
            boolean relevante = esEstatico || VERBOSO_METODOS || n.indexOf("ackage") >= 0 || n.indexOf("uid") >= 0;
            if (!relevante) continue;
            String extra = "";
            try {
                if (esEstatico) { f.setAccessible(true); Object v = f.get(null); if (v != null) extra = " = " + v; }
            } catch (Throwable t) { }
            System.out.println("  campo " + (esEstatico ? "static" : "instancia") + " " + f.getType().getName() + " " + n + extra);
        }
        for (Method m : c.getDeclaredMethods()) {
            if (!VERBOSO_METODOS) {
                String n = m.getName();
                if (n.indexOf("ackage") < 0 && n.indexOf("Uid") < 0 && n.indexOf("uid") < 0) continue;
            }
            System.out.println("  metodo " + Modifier.toString(m.getModifiers()) + " " + m.getName() + " " + java.util.Arrays.toString(m.getParameterTypes()) + " -> " + m.getReturnType().getName());
        }
        System.out.flush();
    }

    /** Solo enumera lo que el dispositivo REALMENTE reporta. No abre la camara. */
    private static void probe() throws Exception {
        Context ctx = Acquire.contextoShell(Acquire.context());
        Acquire.declararPaqueteShell(ctx);
        CameraManager m = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
        Acquire.corregirPaqueteDelActivityThread();
        Acquire.declararAtribucionShell(m);
        List<CamInfo> cams = enumerate(m);
        StringBuilder sb = new StringBuilder();
        sb.append("{\"probe\":true,\"sdk\":").append(Build.VERSION.SDK_INT)
          .append(",\"release\":").append(q(Build.VERSION.RELEASE))
          .append(",\"model\":").append(q(Build.MODEL))
          .append(",\"ctxHow\":").append(q(Acquire.how))
          .append(",\"cameraCount\":").append(cams.size())
          .append(",\"cameras\":[");
        for (int i = 0; i < cams.size(); i++) { if (i > 0) sb.append(','); sb.append(cams.get(i).json()); }
        sb.append("]}");
        log(sb.toString());
        System.out.flush();
    }

    /** Prueba directa de openCamera: es el camino que de verdad importa y es distinto
     *  del de getCameraCharacteristics. No transmite nada. */
    private static void abrirCamara() throws Exception {
        Context ctx = Acquire.contextoShell(Acquire.context());
        Acquire.declararPaqueteShell(ctx);
        CameraManager m = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
        Acquire.corregirPaqueteDelActivityThread();
        Acquire.declararAtribucionShell(m);
        if (m == null) { log("no hay CameraManager"); return; }

        String[] ids = m.getCameraIdList();
        log("camaras: " + java.util.Arrays.toString(ids));

        HandlerThread ht = new HandlerThread("abrir");
        ht.start();
        Handler h = new Handler(ht.getLooper());
        final CountDownLatch latch = new CountDownLatch(1);
        final String[] resultado = new String[1];

        CamCbPrueba cb = new CamCbPrueba(latch, resultado);
        try {
            m.openCamera(ids[0], cb, h);
        } catch (Throwable t) {
            log("openCamera lanzo: " + t);
            t.printStackTrace();
            return;
        }
        boolean ok = latch.await(15, TimeUnit.SECONDS);
        log("RESULTADO openCamera: " + (ok ? "respondio" : "sin respuesta en 15s") + " -> " + resultado[0]);
        ht.quitSafely();
    }

    private static final class CamCbPrueba extends CameraDevice.StateCallback {
        private final CountDownLatch latch;
        private final String[] res;
        CamCbPrueba(CountDownLatch l, String[] r) { latch = l; res = r; }
        @Override public void onOpened(CameraDevice d) {
            res[0] = "ABIERTA id=" + d.getId();
            log(res[0]);
            latch.countDown();
        }
        @Override public void onDisconnected(CameraDevice d) { res[0] = "DESCONECTADA"; latch.countDown(); }
        @Override public void onError(CameraDevice d, int e) { res[0] = "ERROR " + e + " (" + camErr(e) + ")"; latch.countDown(); }
    }

    // ================================================================ run
    private static void run(int videoPort, int ctrlPort) throws Exception {
        log("Lenscord v1 iniciando");
        gCtx = Acquire.contextoShell(Acquire.context());
        log("context: " + Acquire.how + " (" + gCtx.getClass().getName() + ")");
Acquire.declararPaqueteShell(gCtx);

        gCamMgr = (CameraManager) gCtx.getSystemService(Context.CAMERA_SERVICE);
        if (gCamMgr == null) throw new IllegalStateException("CameraService no disponible");
        Acquire.corregirPaqueteDelActivityThread();
        Acquire.declararAtribucionShell(gCamMgr);

        List<CamInfo> cams = enumerate(gCamMgr);
        log("camaras: " + cams.size());
        for (CamInfo c : cams) log("  " + c.log());
        if (cams.isEmpty()) throw new IllegalStateException("el dispositivo no reporto ninguna camara");
        gCamId = pickDefault(cams);

        // Ajusta el formato pedido a algo que la camara realmente soporte
        CamInfo cur = byId(cams, gCamId);
        long[] wh = nearestSize(cur, gWidth, gHeight);
        gWidth = (int) wh[0]; gHeight = (int) wh[1];
        gFps = clampFps(gCamMgr.getCameraCharacteristics(gCamId), gFps);
        gBitrateKbps = Math.max(900, (int) (gWidth * gHeight * 0.10 * gFps / 1000));
        log("formato elegido: " + gWidth + "x" + gHeight + "@" + gFps
                + " " + gBitrateKbps + "kbps cam=" + gCamId);

        gCamThread = new HandlerThread("lenscord-cam");
        gCamThread.start();
        gCamHandler = new Handler(gCamThread.getLooper());

        ServerSocket videoSrv = new ServerSocket(videoPort, 1, InetAddress.getByName("127.0.0.1"));
        ServerSocket ctrlSrv = new ServerSocket(ctrlPort, 1, InetAddress.getByName("127.0.0.1"));
        log("escuchando video=" + videoPort + " control=" + ctrlPort);

        Socket cs = ctrlSrv.accept();
        openCtrl(cs);
        hello(cams);

        Socket vs = videoSrv.accept();
        OutputStream vout = new BufferedOutputStream(vs.getOutputStream(), 1 << 16);
        log("PC conectada desde " + vs.getRemoteSocketAddress());

        gEnc = new StreamEncoder(vout);
        startEncoder();
        openCameraAndSession();

        log("TRANSMITIENDO");
        long t0 = 0;
        while (gRunning) {
            Thread.sleep(120);
            long now = System.currentTimeMillis();
            if (now - t0 >= 1000) {
                t0 = now;
                long n = gEnc.takeFrames();
                ctrl("{\"t\":\"stats\",\"fps\":" + f(n) + ",\"total\":" + gEnc.total()
                        + ",\"dropped\":" + gEnc.dropped() + "}");
            }
        }

        log("parando");
        stopAll();
        try { videoSrv.close(); } catch (Throwable ignored) { }
        try { ctrlSrv.close(); } catch (Throwable ignored) { }
        log("fin");
    }

    // ================================================================ encoder
    private static void startEncoder() throws Exception {
        MediaFormat fmt = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, gWidth, gHeight);
        fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        fmt.setInteger(MediaFormat.KEY_BIT_RATE, gBitrateKbps * 1000);
        fmt.setInteger(MediaFormat.KEY_FRAME_RATE, gFps);
        fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);

        gCodec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
        try {
            gCodec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
        } catch (Throwable t) {
            // sin KEY_PROFILE/LEVEL algunos codec fallan: reintentamos sin restrictores
            log("configure fallo (" + t + "), reintento minimo");
            MediaFormat f2 = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, gWidth, gHeight);
            f2.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            f2.setInteger(MediaFormat.KEY_BIT_RATE, gBitrateKbps * 1000);
            f2.setInteger(MediaFormat.KEY_FRAME_RATE, gFps);
            gCodec.reset();
            gCodec.configure(f2, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
        }
        gEncSurface = gCodec.createInputSurface();
        gCodec.start();
        gEnc.start(gCodec);
        log("encoder H.264 " + gWidth + "x" + gHeight + "@" + gFps
                + " superficieValida=" + (gEncSurface != null && gEncSurface.isValid()));
    }

    /** Convierte los buffers del encoder (NAL con longitud) a Annex-B y los manda. */
    private static final class StreamEncoder {
        private final OutputStream out;
        private final MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        private final AtomicLong frames = new AtomicLong(0);
        private final AtomicLong total = new AtomicLong(0);
        private final AtomicLong dropped = new AtomicLong(0);
        private final AtomicBoolean csdDone = new AtomicBoolean(false);
        private volatile MediaCodec codec;
        private volatile boolean alive = true;
        private Thread pump;
        private boolean firstOut = true;
        private boolean firstWait = true;
        private int outCount = 0;

        StreamEncoder(OutputStream out) { this.out = out; }

        void start(MediaCodec c) {
            codec = c;
            csdDone.set(false);
            pump = new Thread(new EncPump(this), "lenscord-enc");
            pump.setDaemon(true);
            pump.start();
        }

        long takeFrames() { return frames.getAndSet(0); }
        long total() { return total.get(); }
        long dropped() { return dropped.get(); }

        private void loop() {
            try {
                while (alive && gRunning) {
                    MediaCodec c = codec;
                    if (c == null) { Thread.sleep(5); continue; }
                    int idx = c.dequeueOutputBuffer(info, 10000);
                    if (idx >= 0) {
                        outCount++;
                        if (firstOut || outCount <= 5 || outCount % 30 == 0) {
                            firstOut = false;
                            log("buffer encoder n=" + outCount + " idx=" + idx
                                    + " size=" + info.size + " flags=" + info.flags);
                        }
                        ByteBuffer buf = c.getOutputBuffer(idx);
                        int size = info.size;
                        if (buf != null && size > 0) {
                            byte[] data = new byte[size];
                            buf.position(info.offset);
                            buf.limit(info.offset + size);
                            buf.get(data);
                            boolean config = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                            if (!config) {
                                writeAnnexB(data);
                                total.incrementAndGet();
                                frames.incrementAndGet();
                            } else if (!csdDone.get()) {
                                writeAnnexB(data);
                            }
                        }
                        boolean eos = (info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                        c.releaseOutputBuffer(idx, false);
                        if (eos) break;
                    } else if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        writeCsd(c.getOutputFormat());
                        csdDone.set(true);
                    } else if (idx == MediaCodec.INFO_TRY_AGAIN_LATER && firstWait) {
                        firstWait = false;
                        log("encoder sin buffers todavia (TRY_AGAIN_LATER)");
                    }
                    // INFO_TRY_AGAIN_LATER: no hay nada que hacer, el bucle ya espera
                }
            } catch (Throwable t) {
                log("bomba del encoder fallo: " + t);
            }
        }

        private static boolean esAnnexB(byte[] d) {
            return d.length >= 4 && d[0] == 0 && d[1] == 0
                    && (d[2] == 1 || (d[2] == 0 && d[3] == 1));
        }

        private void writeAnnexB(byte[] d) throws Exception {
            // Algunos encoders (Samsung) ya entregan Annex-B; otros usan
            // longitud de 4 bytes. Distinguir es obligatorio: confundir el
            // start code con una longitud corrompe SPS/PPS y los frames.
            if (esAnnexB(d)) {
                out.write(d);
                out.flush();
                return;
            }
            int i = 0;
            boolean any = false;
            while (i + 4 <= d.length) {
                int len = ((d[i] & 0xff) << 24) | ((d[i + 1] & 0xff) << 16)
                        | ((d[i + 2] & 0xff) << 8) | (d[i + 3] & 0xff);
                if (len <= 0 || i + 4 + len > d.length) {   // no es longitud-prefijado
                    out.write(START); out.write(d, i, d.length - i); any = true;
                    break;
                }
                out.write(START); out.write(d, i + 4, len); any = true;
                i += 4 + len;
            }
            if (!any && i < d.length) { out.write(START); out.write(d, i, d.length - i); }
            else if (i == d.length && !any) { out.write(d); }
            out.flush();
        }

        private void writeCsd(MediaFormat fmt) throws Exception {
            for (String k : new String[]{"csd-0", "csd-1"}) {
                if (!fmt.containsKey(k)) continue;
                ByteBuffer b = fmt.getByteBuffer(k);
                if (b == null) continue;
                byte[] d = new byte[b.remaining()];
                b.get(d);
                writeAnnexB(d);
            }
            out.flush();
            log("SPS/PPS enviados");
        }

        void release() {
            alive = false;
            MediaCodec c = codec; codec = null;
            if (c != null) { try { c.stop(); } catch (Throwable ignored) { } try { c.release(); } catch (Throwable ignored) { } }
        }

        void closeStream() {
            try { out.flush(); } catch (Throwable ignored) { }
            try { out.close(); } catch (Throwable ignored) { }
        }
    }

    /** Clase con nombre, no anonima, a proposito: d8 (build-tools 34, r8 8.2.2)
     *  tira NullPointerException al leer el atributo InnerClasses de una clase
     *  anonima declarada DENTRO de una clase anidada. */
    private static final class EncPump implements Runnable {
        private final StreamEncoder enc;
        EncPump(StreamEncoder e) { enc = e; }
        @Override public void run() { enc.loop(); }
    }

// ================================================================ camara
    private static final CountDownLatch gAbierto = new CountDownLatch(1);
    private static final boolean[] gAperturaOk = new boolean[]{false};
    private static final CountDownLatch gConfigOk = new CountDownLatch(1);
    private static final boolean[] gSesionOk = new boolean[]{false};

    /** Callbacks con nombre, no anonimos: d8 8.2.2 tira NPE con InnerClasses anonimos. */
    private static final class CamCb extends CameraDevice.StateCallback {
        @Override public void onOpened(CameraDevice d) { gDevice = d; gAperturaOk[0] = true; gAbierto.countDown(); }
        @Override public void onDisconnected(CameraDevice d) { log("camara desconectada"); gAbierto.countDown(); }
        @Override public void onError(CameraDevice d, int e) {
            log("ERROR de camara: " + camErr(e));
            ctrl("{\"t\":\"error\",\"m\":" + q("camara: " + camErr(e)) + "}");
            gAbierto.countDown();
        }
    }

    private static final class SesionCb extends CameraCaptureSession.StateCallback {
        @Override public void onConfigured(CameraCaptureSession s) { gSession = s; gSesionOk[0] = true; gConfigOk.countDown(); }
        @Override public void onReady(CameraCaptureSession s) { log("sesion onReady"); }
        @Override public void onConfigureFailed(CameraCaptureSession s) {
            log("ERROR: la camara no acepto la superficie del codificador");
            ctrl("{\"t\":\"error\",\"m\":" + q("la camara no acepto la superficie del codificador") + "}");
            gConfigOk.countDown();
        }
    }

    private static final class CaptureCb extends CameraCaptureSession.CaptureCallback {
        private boolean first = true;
        private int completed = 0;
        @Override public void onCaptureStarted(CameraCaptureSession s, CaptureRequest r, long ts, long frame) {
            if (first) { first = false; log("captura iniciada"); }
        }
        @Override public void onCaptureCompleted(CameraCaptureSession s, CaptureRequest r, TotalCaptureResult res) {
            completed++;
            if (completed <= 3 || completed % 30 == 0) {
                log("captura completada n=" + completed + " frame=" + res.getFrameNumber()
                        + " ts=" + res.get(CaptureResult.SENSOR_TIMESTAMP));
            }
        }
        @Override public void onCaptureFailed(CameraCaptureSession s, CaptureRequest r, CaptureFailure f) {
            log("captura fallo: reason=" + f.getReason() + " frame=" + f.getFrameNumber());
        }
        @Override public void onCaptureBufferLost(CameraCaptureSession s, CaptureRequest r, Surface target, long frame) {
            log("captura PERDIO buffer: frame=" + frame + " targetValido=" + (target != null && target.isValid()));
        }
    }

    private static final CaptureCb gCaptureCb = new CaptureCb();

    private static final class CtrlPump implements Runnable {
        @Override public void run() { readLoop(); }
    }

    private static void openCameraAndSession() throws Exception {
        gAperturaOk[0] = false;
        gSesionOk[0] = false;
        gCamMgr.openCamera(gCamId, new CamCb(), gCamHandler);

        if (!gAbierto.await(8, TimeUnit.SECONDS)) throw new IllegalStateException("timeout abriendo camara " + gCamId);
        if (!gAperturaOk[0]) throw new IllegalStateException("no se pudo abrir la camara " + gCamId
                + " (revisar autorizaciones del sistema o app en uso)");

        CameraCharacteristics ch = gCamMgr.getCameraCharacteristics(gCamId);
        Integer facing = ch.get(CameraCharacteristics.LENS_FACING);
        log("camara abierta " + gCamId + " facing=" + facingName(facing));

        gDevice.createCaptureSession(Arrays.asList(gEncSurface), new SesionCb(), gCamHandler);

        if (!gConfigOk.await(8, TimeUnit.SECONDS) || !gSesionOk[0]) {
            throw new IllegalStateException("no se pudo configurar la sesion de captura");
        }
        repeating();
        log("sesion lista");
    }

    private static String camErr(int e) {
        switch (e) {
            case CameraDevice.StateCallback.ERROR_CAMERA_IN_USE: return "EN USO por otra app";
            case CameraDevice.StateCallback.ERROR_MAX_CAMERAS_IN_USE: return "ya hay el maximo de camaras abiertas";
            case CameraDevice.StateCallback.ERROR_CAMERA_DISABLED: return "DESHABILITADA por politica del sistema";
            case CameraDevice.StateCallback.ERROR_CAMERA_DEVICE: return "FALLO del dispositivo";
            case CameraDevice.StateCallback.ERROR_CAMERA_SERVICE: return "FALLO del servicio de camara";
            default: return "codigo " + e;
        }
    }

    private static void repeating() {
        if (gSession == null || gDevice == null) return;
        if (gEncSurface == null) {
            log("ERROR repeating: no hay superficie del encoder");
            return;
        }
        try {
            CameraCharacteristics ch = gCamMgr.getCameraCharacteristics(gCamId);
            CaptureRequest.Builder b = gDevice.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
            b.addTarget(gEncSurface);
            b.set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO);
            b.set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON);
            b.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
            if (hasFlash()) {
                b.set(CaptureRequest.FLASH_MODE, gTorch
                        ? CaptureRequest.FLASH_MODE_TORCH : CaptureRequest.FLASH_MODE_OFF);
            }
            applyZoom(b, ch);
            long dur = frameDuration(ch);
            if (dur > 0) b.set(CaptureRequest.SENSOR_FRAME_DURATION, dur);
            int seq = gSession.setRepeatingRequest(b.build(), gCaptureCb, gCamHandler);
            log("repeating solicitado seq=" + seq + " frameDur=" + dur + " zoom=" + gZoomFrac);
        } catch (Throwable t) {
            log("ERROR repeating: " + t);
            ctrl("{\"t\":\"error\",\"m\":" + q("no se pudo aplicar la peticion de captura: " + t) + "}");
        }
    }

    private static long frameDuration(CameraCharacteristics ch) {
        try {
            // Clave real: Key<Long> con la DURACION MAXIMA en nanosegundos (o sea, el fps minimo).
            Long maxDur = ch.get(CameraCharacteristics.SENSOR_INFO_MAX_FRAME_DURATION);
            long want = 1000000000L / Math.max(1, gFps);
            if (maxDur != null && want > maxDur) want = maxDur;
            return want / 1000L * 1000L;
        } catch (Throwable t) { return 0; }
    }

    /** Zoom del sensor si el dispositivo lo expone (puede cambiar de lente = optico);
     *  si no, recorte digital sobre el area activa del sensor. */
    private static void applyZoom(CaptureRequest.Builder b, CameraCharacteristics ch) {
        try {
            Range<Float> rr = ch.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE);
            Float maxD = ch.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM);
            Rect active = ch.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE);

            if (rr != null && maxD != null) {
                float min = rr.getLower(), max = rr.getUpper();
                float target = min + (max - min) * gZoomFrac;
                if (target > max) target = max;
                if (target < min) target = min;
                b.set(CaptureRequest.CONTROL_ZOOM_RATIO, target);
                return;
            }
            if (active != null && maxD != null && maxD > 1f) {
                float scale = 1.0f / Math.max(0.05f, gZoomFrac);
                int w = (int) Math.round(active.width() / scale);
                int h = (int) Math.round(active.height() / scale);
                w = Math.max(1, Math.min(active.width(), w));
                h = Math.max(1, Math.min(active.height(), h));
                int cx = active.centerX(), cy = active.centerY();
                int x = Math.max(active.left, Math.min(active.right - w, cx - w / 2));
                int y = Math.max(active.top, Math.min(active.bottom - h, cy - h / 2));
                b.set(CaptureRequest.SCALER_CROP_REGION, new Rect(x, y, x + w, y + h));
            }
        } catch (Throwable t) { log("zoom fallo: " + t); }
    }

    /** "optico" si hay camaras fisicasreportedas; si no, digital. */
    private static String zoomKind() {
        try {
            CameraCharacteristics ch = gCamMgr.getCameraCharacteristics(gCamId);
            Set<String> phys = ch.getPhysicalCameraIds();
            if (phys != null && !phys.isEmpty()) return "sensor";
            return "digital";
        } catch (Throwable t) { return "digital"; }
    }

    private static boolean hasFlash() {
        try {
            Boolean f = gCamMgr.getCameraCharacteristics(gCamId).get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
            return f != null && f;
        } catch (Throwable t) { return false; }
    }

    // ================================================================ control
private static void openCtrl(Socket s) throws Exception {
        gCtrlOut = new BufferedOutputStream(s.getOutputStream(), 8192);
        gCtrlIn = new BufferedReader(new InputStreamReader(s.getInputStream(), "UTF-8"));
        Thread t = new Thread(new CtrlPump(), "lenscord-ctrl");
        t.setDaemon(true);
        t.start();
    }

    private static void readLoop() {
        try {
            String line;
            while (gRunning && (line = gCtrlIn.readLine()) != null) {
                line = line.trim();
                if (line.length() == 0) continue;
                onCtrl(line);
            }
        } catch (Throwable e) {
            log("canal de control terminado: " + e);
            gRunning = false;
        }
    }

    private static void onCtrl(String line) {
        synchronized (gLock) {
            try {
                log("PC-> " + line);
                String cmd = jstr(line, "cmd");
                if (cmd == null) return;

                if (cmd.equals("cameras")) {
                    hello(enumerate(gCamMgr));
                } else if (cmd.equals("zoom")) {
                    gZoomFrac = (float) Math.max(0.0, Math.min(1.0, jnum(line, "value", 1.0)));
                    repeating();
                    state();
                } else if (cmd.equals("torch")) {
                    boolean on = jbool(line, "on");
                    if (!hasFlash()) {
                        ctrl("{\"t\":\"nack\",\"cmd\":\"torch\",\"m\":" + q("este modulo no tiene flash") + "}");
                    } else {
                        gTorch = on; repeating(); state();
                    }
                } else if (cmd.equals("camera")) {
                    String id = jstr(line, "id");
                    if (id != null && !id.equals(gCamId)) switchCamera(id);
                    else state();
                } else if (cmd.equals("format")) {
                    int w = (int) jnum(line, "w", gWidth);
                    int h = (int) jnum(line, "h", gHeight);
                    int fp = (int) jnum(line, "fps", gFps);
                    if (w == gWidth && h == gHeight && fp == gFps) state();
                    else reconfigure(w, h, fp);
                } else if (cmd.equals("pause")) {
                    gPaused = jbool(line, "on");
                    if (gSession != null) {
                        if (gPaused) { gSession.stopRepeating(); log("PAUSA"); }
                        else { repeating(); log("REANUDADO"); }
                    }
                    state();
                } else if (cmd.equals("stop")) {
                    gRunning = false;
                }
            } catch (Throwable t) {
                log("ERROR de control: " + t);
                ctrl("{\"t\":\"error\",\"m\":" + q(String.valueOf(t)) + "}");
            }
        }
    }

    private static void switchCamera(String id) throws Exception {
        ctrl("{\"t\":\"busy\",\"m\":" + q("Reiniciando camara...") + "}");
        closeCam();
        gCamId = id;
        gZoomFrac = 1.0f;
        gPaused = false;
        openCameraAndSession();
        state();
    }

    private static void reconfigure(int w, int h, int fp) throws Exception {
        ctrl("{\"t\":\"busy\",\"m\":" + q("Reiniciando camara...") + "}");
        closeCam();
        gEnc.release();
        if (gEncSurface != null) { try { gEncSurface.release(); } catch (Throwable ignored) { } gEncSurface = null; }
        gCodec = null;

        CameraCharacteristics ch = gCamMgr.getCameraCharacteristics(gCamId);
        List<CamInfo> tmp = new ArrayList<CamInfo>();
        CamInfo fake = byId(enumerate(gCamMgr), gCamId);
        long[] wh = nearestSize(fake, w, h);
        gWidth = (int) wh[0]; gHeight = (int) wh[1];
        gFps = clampFps(ch, fp);
        gBitrateKbps = Math.max(900, (int) (gWidth * gHeight * 0.10 * gFps / 1000));
        startEncoder();
        openCameraAndSession();
        state();
    }

    private static void closeCam() {
        try { if (gSession != null) { gSession.stopRepeating(); gSession.close(); } } catch (Throwable ignored) { }
        try { if (gDevice != null) gDevice.close(); } catch (Throwable ignored) { }
        gSession = null; gDevice = null;
    }

    private static void state() {
        ctrl("{\"t\":\"state\",\"camera\":" + q(gCamId)
                + ",\"torch\":" + gTorch
                + ",\"zoom\":" + f(gZoomFrac)
                + ",\"zoomKind\":" + q(zoomKind())
                + ",\"paused\":" + gPaused
                + ",\"flashAvailable\":" + hasFlash()
                + ",\"w\":" + gWidth + ",\"h\":" + gHeight + ",\"fps\":" + gFps + "}");
    }

    private static void hello(List<CamInfo> cams) {
        StringBuilder sb = new StringBuilder();
        sb.append("{\"t\":\"hello\",\"sdk\":").append(Build.VERSION.SDK_INT)
          .append(",\"release\":").append(q(Build.VERSION.RELEASE))
          .append(",\"model\":").append(q(Build.MODEL))
          .append(",\"ctxHow\":").append(q(Acquire.how))
          .append(",\"camera\":").append(q(gCamId))
          .append(",\"torch\":").append(gTorch)
          .append(",\"flashAvailable\":").append(hasFlash())
          .append(",\"zoom\":").append(f(gZoomFrac))
          .append(",\"zoomKind\":").append(q(zoomKind()))
          .append(",\"paused\":false")
          .append(",\"w\":").append(gWidth).append(",\"h\":").append(gHeight).append(",\"fps\":").append(gFps)
          .append(",\"cameras\":[");
        for (int i = 0; i < cams.size(); i++) { if (i > 0) sb.append(','); sb.append(cams.get(i).json()); }
        sb.append("]}");
        ctrl(sb.toString());
    }

    private static synchronized void ctrl(String s) {
        try {
            if (gCtrlOut == null) return;
            gCtrlOut.write(s.getBytes(UTF8));
            gCtrlOut.write('\n');
            gCtrlOut.flush();
        } catch (Throwable t) { log("envio de control fallo: " + t); }
    }

    // ================================================================ inventario
    private static final class CamInfo {
        String id = "";
        String facing = "UNKNOWN";
        int[] wh = new int[0];          // pares [ancho, alto, ancho, alto, ...] de mayor a menor
        float maxDigitalZoom = 1f;
        float ratioMin = 1f, ratioMax = 1f;
        boolean hasRatio = false;
        boolean flash = false;
        String[] physical = new String[0];
        int hwLevel = 0;

        String json() {
            StringBuilder sb = new StringBuilder();
            sb.append("{\"id\":").append(q(id))
              .append(",\"facing\":").append(q(facing))
              .append(",\"sizes\":[");
            for (int i = 0; i < wh.length; i++) { if (i > 0) sb.append(','); sb.append(wh[i]); }
            sb.append("],\"maxDigitalZoom\":").append(f(maxDigitalZoom))
              .append(",\"ratioMin\":").append(f(ratioMin))
              .append(",\"ratioMax\":").append(f(ratioMax))
              .append(",\"hasRatio\":").append(hasRatio)
              .append(",\"flash\":").append(flash)
              .append(",\"hwLevel\":").append(hwLevel)
              .append(",\"physical\":[");
            for (int i = 0; i < physical.length; i++) { if (i > 0) sb.append(','); sb.append(q(physical[i])); }
            sb.append("]}");
            return sb.toString();
        }

        String log() {
            return "id=" + id + " facing=" + facing + " flash=" + flash
                    + " ratio=[" + ratioMin + ".." + ratioMax + "] maxDigitalZoom=" + maxDigitalZoom
                    + " hwLevel=" + hwLevel + " fisicas=" + Arrays.toString(physical)
                    + " sizes=" + pairs(wh);
        }
    }

    private static String pairs(int[] a) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i + 1 < a.length; i += 2) {
            if (i > 0) sb.append("x");
            sb.append(a[i]).append("x").append(a[i + 1]);
        }
        return sb.toString();
    }

    private static List<CamInfo> enumerate(CameraManager m) {
        List<CamInfo> out = new ArrayList<CamInfo>();
        try {
            String[] ids = m.getCameraIdList();
            for (String id : ids) {
                CamInfo c = new CamInfo();
                c.id = id;
                try {
                    CameraCharacteristics ch = m.getCameraCharacteristics(id);
                    Integer fa = ch.get(CameraCharacteristics.LENS_FACING);
                    c.facing = facingName(fa);
                    Boolean fl = ch.get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
                    c.flash = fl != null && fl;
                    Float md = ch.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM);
                    c.maxDigitalZoom = md == null ? 1f : md;
                    Range<Float> rr = ch.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE);
                    if (rr != null) { c.hasRatio = true; c.ratioMin = rr.getLower(); c.ratioMax = rr.getUpper(); }
                    Set<String> pc = ch.getPhysicalCameraIds();
                    if (pc != null) c.physical = pc.toArray(new String[0]);
                    Integer hl = ch.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL);
                    c.hwLevel = hl == null ? 0 : hl;

                    StreamConfigurationMap scm = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
                    if (scm != null) {
                        Size[] st = scm.getOutputSizes(Surface.class);
                        TreeSet<Long> set = new TreeSet<Long>();
                        if (st != null) {
                            for (Size s : st) set.add((long) s.getWidth() * 1000000L + s.getHeight());
                        }
                        // Sin Comparator anonimo: esas clases anonimas genericas generan
                        // un "bridge method" sintetico que hace fallar a d8 con NPE.
                        // El TreeSet ya viene en orden natural ascendente, asi que se
                        // recorre al reves para dejar la lista de mayor a menor.
                        List<Long> asc = new ArrayList<Long>(set);
                        List<Integer> flat = new ArrayList<Integer>();
                        int limit = Math.min(asc.size(), 12);
                        for (int k = 0; k < limit; k++) {
                            long v = asc.get(asc.size() - 1 - k);
                            flat.add((int) (v / 1000000L));
                            flat.add((int) (v % 1000000L));
                        }
                        int[] arr = new int[flat.size()];
                        for (int i = 0; i < arr.length; i++) arr[i] = flat.get(i);
                        c.wh = arr;
                    }
                } catch (Throwable t) {
                    log("camara " + id + ": no se pudieron leer sus datos (" + t + ")");
                }
                out.add(c);
            }
        } catch (Throwable t) {
            log("getCameraIdList fallo: " + t);
            ctrl("{\"t\":\"error\",\"m\":" + q("no se pudo listar camaras: " + t) + "}");
        }
        return out;
    }

    private static CamInfo byId(List<CamInfo> l, String id) {
        for (CamInfo c : l) if (c.id.equals(id)) return c;
        return new CamInfo();
    }

    private static String pickDefault(List<CamInfo> cams) {
        for (CamInfo c : cams) if ("BACK".equals(c.facing)) return c.id;
        return cams.get(0).id;
    }

    /** Elige la resolucion reportada mas cercana a la pedida. */
    private static long[] nearestSize(CamInfo c, int w, int h) {
        long[] best = {1280, 720};
        long bestScore = Long.MAX_VALUE;
        for (int i = 0; i + 1 < c.wh.length; i += 2) {
            int cw = c.wh[i], chh = c.wh[i + 1];
            long score = Math.abs(cw - w) + Math.abs(chh - h);
            if (score < bestScore) { bestScore = score; best = new long[]{cw, chh}; }
        }
        return best;
    }

    /** Ajusta los fps pedidos a un rango AE realmente disponible. */
    private static int clampFps(CameraCharacteristics ch, int want) {
        try {
            // Clave real: Key<Range<Integer>[]>, no un int[] plano.
            Range<Integer>[] ranges = ch.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
            if (ranges == null || ranges.length == 0) return want;
            int best = 0;
            for (Range<Integer> r : ranges) {
                int lower = r.getLower() == null ? 0 : r.getLower();
                if (lower <= want && lower > best) best = lower;
            }
            if (best == 0) {
                int min = Integer.MAX_VALUE;
                for (Range<Integer> r : ranges) {
                    if (r.getLower() == null) continue;
                    min = Math.min(min, r.getLower());
                }
                if (min == Integer.MAX_VALUE) return want;
                best = min;
            }
            return best;
        } catch (Throwable t) { return want; }
    }

    private static String facingName(Integer f) {
        if (f == null) return "UNKNOWN";
        if (f == CameraCharacteristics.LENS_FACING_BACK) return "BACK";
        if (f == CameraCharacteristics.LENS_FACING_FRONT) return "FRONT";
        if (f == CameraCharacteristics.LENS_FACING_EXTERNAL) return "EXTERNAL";
        return "UNKNOWN";
    }

    // ================================================================ apagado
    private static void stopAll() {
        closeCam();
        if (gEnc != null) gEnc.release();
        if (gEncSurface != null) { try { gEncSurface.release(); } catch (Throwable ignored) { } }
        try {
            if (gCtrlOut != null) { gCtrlOut.write("{\"t\":\"bye\"}\n".getBytes(UTF8)); gCtrlOut.flush(); }
        } catch (Throwable ignored) { }
        if (gEnc != null) gEnc.closeStream();
        if (gCamThread != null) gCamThread.quitSafely();
        log("limpieza completada");
    }

    // ================================================================ utils
    private static void log(String s) {
        System.out.println("[" + TAG + "] " + s);
        System.out.flush();
        try { Log.i(TAG, s); } catch (Throwable ignored) { }
    }

    private static String q(String s) { return "\"" + esc(s) + "\""; }

    private static String esc(String s) {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            if (ch == '"' || ch == '\\') sb.append('\\').append(ch);
            else if (ch < 0x20) sb.append(' ');
            else sb.append(ch);
        }
        return sb.toString();
    }

    private static String f(double d) { return String.format(Locale.US, "%.4f", d); }

    private static String jstr(String j, String key) {
        String k = "\"" + key + "\"";
        int i = j.indexOf(k);
        if (i < 0) return null;
        int a = j.indexOf(':', i + k.length());
        if (a < 0) return null;
        int b = j.indexOf('"', a + 1);
        if (b < 0) return null;
        int e = j.indexOf('"', b + 1);
        if (e < 0) return null;
        return j.substring(b + 1, e);
    }

    private static double jnum(String j, String key, double def) {
        String k = "\"" + key + "\"";
        int i = j.indexOf(k);
        if (i < 0) return def;
        int a = j.indexOf(':', i + k.length());
        if (a < 0) return def;
        int p = a + 1;
        while (p < j.length() && j.charAt(p) == ' ') p++;
        int s = p;
        while (p < j.length() && "0123456789+-.eE".indexOf(j.charAt(p)) >= 0) p++;
        if (s == p) return def;
        try { return Double.parseDouble(j.substring(s, p)); } catch (Throwable t) { return def; }
    }

    private static boolean jbool(String j, String key) {
        String k = "\"" + key + "\"";
        int i = j.indexOf(k);
        if (i < 0) return false;
        int a = j.indexOf(':', i + k.length());
        if (a < 0) return false;
        return j.substring(a + 1).trim().startsWith("true");
    }
}
