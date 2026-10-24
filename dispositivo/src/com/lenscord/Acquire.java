package com.lenscord;

import android.content.Context;

import java.lang.reflect.Constructor;
import java.lang.reflect.Method;

/**
 * Consigue un Context de sistema en un proceso app_process "tool" (sin ActivityThread).
 *
 * Un proceso lanzado como
 *   app_process /data/local/tmp com.lenscord.AlgunaClase
 * entra por RuntimeInit.main() en modo "tool": no hay ActivityThread, no hay Application
 * y por tanto no hay Context. CameraManager, sin embargo, se obtiene del Context.
 *
 * Probamos, en orden, las vias reales que existen en AOSP y paramos en la primera que
 * funcione. Cada intento queda registrado para poder reportar con evidencia cual sirvio.
 */
final class Acquire {

    /** Como se logro. Se imprime al log y viaja en el "hello" para la PC. */
    static String how = "ninguna";
    private static Context gCtx;
    private static Object gActivityThread;

    /**
     * El Context de sistema se declara con el paquete "android", pero nuestro UID es
     * 2000 (shell). AppOps valida que el paquete pertenezca al UID y responde
     * "Permission Denial: package=android does not belong to uid=2000".
     * Declarando el proceso como "com.android.shell" (que SI es del UID 2000)
     * la validacion pasa. Se hace por reflexion: es metodo estatico y @hide.
     */
    /**
     * En Android 16 la identidad de paquete que usa Camera2 ya no sale del Context sino de
     * un android.content.AttributionSourceState, que CameraManager expone con
     * getClientAttribution(). Ese objeto trae packageName="android" y uid del sistema,
     * y cameraserver lo valida: "package=android does not belong to uid=2000".
     * Se reescribe a com.android.shell / uid 2000, que es lo que realmente somos.
     */
    /**
     * getOpPackageName() ignora los campos del propio ContextImpl y lo lee del
     * ActivityThread (mBoundApplication.info). Contexto de sistema => "android".
     * Se reescribe ahi, que es de donde sale el "android" que cameraserver rechaza.
     */
    static void corregirPaqueteDelActivityThread() {
        try {
            Class<?> at = Class.forName("android.app.ActivityThread");
            Method cur = at.getDeclaredMethod("currentActivityThread");
            cur.setAccessible(true);
            Object inst = cur.invoke(null);
            if (inst == null) { log("currentActivityThread() sigue siendo null"); return; }

            java.lang.reflect.Field ba = at.getDeclaredField("mBoundApplication");
            ba.setAccessible(true);
            Object bound = ba.get(inst);
            if (bound == null) { log("mBoundApplication es null"); return; }

            String[] campos = {"info", "appInfo"};
            for (String c : campos) {
                try {
                    java.lang.reflect.Field f = bound.getClass().getDeclaredField(c);
                    f.setAccessible(true);
                    Object appInfo = f.get(bound);
                    if (appInfo == null) continue;
                    java.lang.reflect.Field pn = appInfo.getClass().getField("packageName");
                    pn.set(appInfo, "com.android.shell");
                    java.lang.reflect.Field uid = appInfo.getClass().getField("uid");
                    uid.setInt(appInfo, 2000);
                    log("mBoundApplication." + c + ".packageName -> " + pn.get(appInfo));
                } catch (Throwable t) {
                    log("no se pudo cambiar mBoundApplication." + c + ": " + t);
                }
            }
            Method op = at.getDeclaredMethod("currentOpPackageName");
            op.setAccessible(true);
            log("currentOpPackageName ahora: " + op.invoke(null));
        } catch (Throwable t) {
            log("no se pudo corregir el paquete del ActivityThread: " + t);
        }
    }

    static void declararAtribucionShell(Object cameraManager) {
        if (cameraManager == null) return;
        try {
            Method get = cameraManager.getClass().getMethod("getClientAttribution");
            Object as = get.invoke(cameraManager);
            if (as == null) { log("getClientAttribution() devolvio null"); return; }

            Class<?> c = as.getClass();
            java.lang.reflect.Field p = c.getDeclaredField("packageName");
            p.setAccessible(true);
            p.set(as, "com.android.shell");

            java.lang.reflect.Field u = c.getDeclaredField("uid");
            u.setAccessible(true);
            u.set(as, Integer.valueOf(2000));

            log("atribucion reescrita: " + p.get(as) + " / uid " + u.get(as));
        } catch (Throwable t) {
            log("no se pudo reescribir la atribucion: " + t);
        }
    }

    static void declararPaqueteShell(Context ctx) {
        if (ctx == null) return;
        // En Android 16 (este telefono) no existe ActivityThread.setCurrentProcessPackageName.
        // El paquete de operacion sale de ContextImpl.mOpPackageName, que para un Context
        // de sistema vale "android". Como nuestro UID es 2000, AppOps responde
        // "Permission Denial: package=android does not belong to uid=2000".
        // Se sobrescribe el campo por reflexion con un paquete que SI es del UID 2000.
        try {
            Class<?> ci = Class.forName("android.app.ContextImpl");
            for (String campo : new String[]{"mOpPackageName", "mBasePackageName"}) {
                try {
                    java.lang.reflect.Field f = ci.getDeclaredField(campo);
                    f.setAccessible(true);
                    f.set(ctx, "com.android.shell");
                } catch (Throwable t) {
                    log("no se pudo cambiar " + campo + ": " + t);
                }
            }
            Method get = ctx.getClass().getMethod("getOpPackageName");

            // Android 16: getOpPackageName() ya no lee mOpPackageName (que ademas
            // es final), sino mAttributionSource. Hay que corregir la atribucion
            // real que viaja al servicio de camara.
            try {
                java.lang.reflect.Field af = ci.getDeclaredField("mAttributionSource");
                af.setAccessible(true);
                Object as = af.get(ctx);
                if (as != null) {
                    java.lang.reflect.Field sf = as.getClass().getDeclaredField("mAttributionSourceState");
                    sf.setAccessible(true);
                    Object state = sf.get(as);
                    if (state != null) {
                        java.lang.reflect.Field pn = state.getClass().getDeclaredField("packageName");
                        pn.setAccessible(true);
                        pn.set(state, "com.android.shell");
                        java.lang.reflect.Field uid = state.getClass().getDeclaredField("uid");
                        uid.setAccessible(true);
                        uid.setInt(state, 2000);
                        java.lang.reflect.Field pid = state.getClass().getDeclaredField("pid");
                        pid.setAccessible(true);
                        pid.setInt(state, android.os.Process.myPid());
                    }
                    log("atribucion del Context: " + as);
                }
            } catch (Throwable t) {
                log("no se pudo corregir mAttributionSource: " + t);
            }

            log("getOpPackageName tras mOpPackageName: " + get.invoke(ctx));

            // getOpPackageName() puede derivar de mPackageInfo (LoadedApk) ->
            // mApplicationInfo.packageName. Se reescribe tambien ahi.
            try {
                java.lang.reflect.Field pi = ci.getDeclaredField("mPackageInfo");
                pi.setAccessible(true);
                Object loadedApk = pi.get(ctx);
                if (loadedApk != null) {
                    java.lang.reflect.Field ai = loadedApk.getClass().getDeclaredField("mApplicationInfo");
                    ai.setAccessible(true);
                    Object appInfo = ai.get(loadedApk);
                    if (appInfo != null) {
                        java.lang.reflect.Field pn = appInfo.getClass().getField("packageName");
                        pn.set(appInfo, "com.android.shell");
                        log("LoadedApk.mApplicationInfo.packageName -> " + pn.get(appInfo));
                    }
                }
            } catch (Throwable t) {
                log("no se pudo cambiar LoadedApk: " + t);
            }
            log("getOpPackageName final: " + get.invoke(ctx));
        } catch (Throwable t) {
            log("no se pudo declarar el paquete: " + t);
        }
    }

    /**
     * Un proceso app_process en modo "tool" entra por RuntimeInit.main() y su hilo
     * principal NO tiene Looper preparado. ActivityThread crea un Handler en el hilo
     * principal al construirse, y por eso falla con
     * "Can't create handler inside thread that has not called Looper.prepare()".
     * Preparando el Looper principal antes de tocar ActivityThread se resuelve.
     */
    static void prepararLooperPrincipal() {
        try {
            if (android.os.Looper.getMainLooper() == null) {
                android.os.Looper.prepareMainLooper();
                log("Looper principal preparado");
            }
        } catch (Throwable t) {
            log("no se pudo preparar el Looper principal: " + t);
        }
    }

    private Acquire() { }

    static Context context() throws Exception {
        Throwable last = null;

        // 1) ActivityThread ya vivo en este hilo (p.ej. si alguien nos lanzo desde una app)
        try {
            Class<?> at = Class.forName("android.app.ActivityThread");
            Method cur = at.getDeclaredMethod("currentActivityThread");
            cur.setAccessible(true);
            Object inst = cur.invoke(null);
            Context c = systemContext(at, inst);
            if (c != null) { gActivityThread = inst; how = "1:ActivityThread.currentActivityThread().getSystemContext()"; return c; }
            last = new IllegalStateException("1: currentActivityThread() devolvio null");
        } catch (Throwable t) { last = unwrap(t, "1"); }
        log("via 1 fallida: " + last);

        // 2) AppGlobals.getInitialApplication()
        try {
            Class<?> ag = Class.forName("android.app.AppGlobals");
            Method m = ag.getDeclaredMethod("getInitialApplication");
            m.setAccessible(true);
            Object app = m.invoke(null);
            if (app instanceof Context) {
                how = "2:AppGlobals.getInitialApplication()";
                return (Context) app;
            }
            last = new IllegalStateException("2: getInitialApplication() devolvio " + app);
        } catch (Throwable t) { last = unwrap(t, "2"); }
        log("via 2 fallida: " + last);

        // 3) Instanciar ActivityThread y pegarle attach() como sistema.
        //    Necesita leer /data/system/packages.xml, que el usuario shell no puede leer.
        Throwable r3 = tryAttach(true, "3");
        if (r3 == null) return gCtx;
        log("via 3 fallida: " + r3);

        // 4) attach(false): variante no-sistema
        Throwable r4 = tryAttach(false, "4");
        if (r4 == null) return gCtx;
        log("via 4 fallida: " + r4);

        throw new IllegalStateException(
                "no se pudo obtener un Context de sistema para CameraManager. Ultimo error: " + r4);
    }

    /** Intenta new ActivityThread() + attach(system). Devuelve null si funciono. */
    private static Throwable tryAttach(boolean system, String tag) {
        try {
            Class<?> at = Class.forName("android.app.ActivityThread");
            Constructor<?> ctor = at.getDeclaredConstructor();
            ctor.setAccessible(true);
            Object inst = ctor.newInstance();
            Method attach = findAttach(at);
            attach.setAccessible(true);
            Object[] argv = new Object[attach.getParameterCount()];
            argv[0] = Boolean.valueOf(system);
            for (int i = 1; i < argv.length; i++) argv[i] = Integer.valueOf(0);
            attach.invoke(inst, argv);
            Context c = systemContext(at, inst);
            if (c != null) {
                gCtx = c;
                gActivityThread = inst;
                how = tag + ":new ActivityThread().attach(" + system + ")";
                return null;
            }
            return new IllegalStateException(tag + ": getSystemContext() devolvio null");
        } catch (Throwable t) {
            return unwrap(t, tag);
        }
    }

    /** Los InvocationTargetException esconden la causa real: hay que desenvolverlos. */
    private static Throwable unwrap(Throwable t, String tag) {
        Throwable cur = t;
        while (cur instanceof java.lang.reflect.InvocationTargetException && cur.getCause() != null)
            cur = cur.getCause();
        String msg = cur.getClass().getSimpleName() + ": " + cur.getMessage();
        if (cur.getStackTrace().length > 0)
            msg += " @ " + cur.getStackTrace()[0];
        return new IllegalStateException(tag + " -> " + msg);
    }

    /**
     * El Context de sistema pertenece al paquete "android" (UID 1000), pero el
     * proceso corre como shell (UID 2000). Android 16 valida esa identidad en
     * CameraManager.getRotationOverride -> ActivityTaskManager.getAppTasks.
     * Un Context del paquete instalado com.android.shell tiene el mismo UID real
     * y evita la suplantacion por reflexion.
     */
    static Context contextoShell(Context base) {
        if (base == null) return null;
        try {
            Context c = base.createPackageContext("com.android.shell", 0);
            declararPaqueteShell(c);
            String pkg = c.getPackageName();
            String op = c.getOpPackageName();
            log("contexto com.android.shell: package=" + pkg + " opPackage=" + op);
            if ("com.android.shell".equals(op)) return c;
            log("el Context de shell no reporta com.android.shell; se usa el de sistema");
        } catch (Throwable t) {
            log("no se pudo crear Context de com.android.shell: " + t);
        }
        return base;
    }

    private static void log(String s) {
        System.out.println("[Acquire] " + s);
        System.out.flush();
    }

    private static Method findAttach(Class<?> at) throws NoSuchMethodException {
        for (Method m : at.getDeclaredMethods()) {
            if (!m.getName().equals("attach")) continue;
            Class<?>[] p = m.getParameterTypes();
            if (p.length >= 1 && p[0] == boolean.class) return m;
        }
        throw new NoSuchMethodException("ActivityThread.attach(boolean,...)");
    }

    private static Context systemContext(Class<?> at, Object inst) {
        try {
            Method m = at.getDeclaredMethod("getSystemContext");
            m.setAccessible(true);
            Object c = m.invoke(inst);
            if (c instanceof Context) return (Context) c;
        } catch (Throwable ignored) { }
        return null;
    }
}