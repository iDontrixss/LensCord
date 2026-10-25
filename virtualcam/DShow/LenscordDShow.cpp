// ---------------------------------------------------------------------------
// Lenscord.VirtualCamera.DShow â€” Filtro DirectShow de captura "Lenscord"
//
// Camara virtual user-mode (sin driver de kernel) para aplicaciones basadas
// en DirectShow/libwebrtc (Discord). Consume el mismo archivo compartido
// %ProgramData%\Lenscord\frames.nv12 que alimenta la camara virtual de
// Media Foundation, por lo que no hay cambios en el codigo de la PC.
//
// Registro: regsvr32 -> DllRegisterServer:
//   1. CLSID propio con InprocServer32 (ThreadingModel Both).
//   2. Instancia en la categoria de camaras CLSID_VideoInputDeviceCategory
//      generada por IFilterMapper2::RegisterFilter (FilterData nativo).
//
// Sin dependencia de las DirectShow Base Classes: COM a mano, mismo patron
// que UnityCapture / obs-virtualcam-filter.
// ---------------------------------------------------------------------------

#define WIN32_LEAN_AND_MEAN
#include <initguid.h>   // instanciacion de los DEFINE_GUID de dshow.h en este TU
#include <dshow.h>
#include <dvdmedia.h>   // VIDEOINFOHEADER2
#include <mmsystem.h>   // timeGetTime
#include <shlobj.h>     // SHGetFolderPathW (CSIDL_COMMON_APPDATA)
#include <new>
#include <memory>
#include <vector>
#include <cstdarg>   // LogDiagLinea (vswprintf_s)

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "shell32.lib")

// ---------------------------------------------------------------------------
// GUIDs propios
// ---------------------------------------------------------------------------

// {5B1F6468-8F22-4A63-B38C-27357AE6D492} â€” filtro Lenscord
DEFINE_GUID(CLSID_LenscordDShow, 0x5B1F6468, 0x8F22, 0x4A63, 0xB3, 0x8C, 0x27, 0x35, 0x7A, 0xE6, 0xD4, 0x92);

// {30323449-0000-0010-8000-00AA00389B71} â€” FOURCC 'I420' (no viene en uuids.h)
DEFINE_GUID(MEDIASUBTYPE_I420_LEN, 0x30323449, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
#define gpSubI420 (&MEDIASUBTYPE_I420_LEN)

// Variante ksproxy de IKsPropertySet (la IID que usan libwebrtc y drivers KS):
// {31EFAC30-515C-11D2-A507-00A0C96A5298}
DEFINE_GUID(IID_IKsPropertySetProxy, 0x31EFAC30, 0x515C, 0x11D2, 0xA5, 0x07, 0x00, 0xA0, 0xC9, 0x6A, 0x52, 0x98);

// {9B00F101-1567-11d1-B3F1-00AA003761C5} â€” AMPROPSETID_Pin
static const GUID s_propSetPin =
    { 0x9B00F101, 0x1567, 0x11D1, { 0xB3, 0xF1, 0x00, 0xAA, 0x00, 0x37, 0x61, 0xC5 } };

// {FB6C4281-0353-11d1-905F-0000C0CC16BA} â€” PINNAME_VIDEO_CAPTURE
static const GUID s_pinCapture =
    { 0xFB6C4281, 0x0353, 0x11D1, { 0x90, 0x5F, 0x00, 0x00, 0xC0, 0xCC, 0x16, 0xBA } };

// GUID nulo (GUARDA: evitamos depender de GUID_NULL del SDK)
static const GUID kGuidNulo = { 0x00000000, 0x0000, 0x0000, { 0, 0, 0, 0, 0, 0, 0, 0 } };

// Diagnostico temporal de entrega (exportado via LenscordDiag()).
// [0..7]: etapas del bucle | [8] ms total ciclo | [9] ms conversion
// [10] ms Receive | [11] ms CopiarUltimo | [12] ms GetBuffer | [13] rondas
// [14] ms espera externa | [15] ms espera ritmo
// [16] QPC(lo) primera iteracion | [17] QPC(lo) ultima iteracion | [18] QPC(lo) tras ultimo Receive
static volatile LONG g_diag[20] = {};

// ---------------------------------------------------------------------------
// Constantes compartidas con pc/Core/VirtualCameraPublisher.cs y
// virtualcam/Source/Shared.cs
// ---------------------------------------------------------------------------
static const int   kAncho       = 1280;
static const int   kAlto        = 720;
static const int   kFps         = 30;
static const int   kTamanoFrame = kAncho * kAlto * 3 / 2;   // 1382400
static const int   kCabecera    = 4096;
static const UINT  kMagia       = 0x46534E4C;               // "LNSF"

static const REFERENCE_TIME kDuracionFrame = 10000000 / kFps;   // 333333 (100 ns)

// maximo de bytes por muestra de salida en la gama admitida (RGB32 720p):
// margen del allocator para SetFormat posteriores a la conexion.
static const int kTamanoSampleMax = kAncho * kAlto * 4;   // 3686400

static const wchar_t kNombreFiltro[]  = L"Lenscord";
static const wchar_t kNombreInstancia[] = L"Lenscord";

// ---------------------------------------------------------------------------
// Utilidades basicas
// ---------------------------------------------------------------------------
struct Seccion
{
    CRITICAL_SECTION cs;
    Seccion() { InitializeCriticalSectionAndSpinCount(&cs, 400); }
    ~Seccion() { DeleteCriticalSection(&cs); }
    void Entra() { EnterCriticalSection(&cs); }
    void Sale() { LeaveCriticalSection(&cs); }
    bool Intentar() { return TryEnterCriticalSection(&cs) != FALSE; }
};

struct Guardia
{
    Seccion* s;
    explicit Guardia(Seccion* p) : s(p) { s->Entra(); }
    ~Guardia() { s->Sale(); }
};

static LONG g_objetosVivos = 0;

static HMODULE g_hModulo = nullptr;

// ---------------------------------------------------------------------------
// Log de diagnostico en archivo: %ProgramData%\Lenscord\dshow-<pid>.log
// El array g_diag solo existe dentro del proceso huesped (Discord) y no se
// puede leer desde afuera; este log persiste lo mismo para post-mortem.
// Sin dependencias nuevas: SHGetFolderPathW + CreateFileW/WriteFile y una
// seccion como lock. Tope 1 MB por archivo: al superarlo se deja de escribir.
// ---------------------------------------------------------------------------
struct LogDiag
{
    Seccion cs;
    HANDLE archivo = nullptr;
    unsigned long long bytes = 0;
    bool techo = false;

    static LogDiag& Presente()
    {
        static LogDiag instancia;
        return instancia;
    }

    static void Ruta(wchar_t* ruta, size_t capacidad)
    {
        ruta[0] = L'\0';
        wchar_t base[MAX_PATH] = L"";
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, 0, base)))
            _snwprintf_s(ruta, capacidad, _TRUNCATE, L"%s\\Lenscord\\dshow-%lu.log", base, GetCurrentProcessId());
    }

    void LineaV(const wchar_t* formato, va_list args)
    {
        wchar_t texto[512];
        _vsnwprintf_s(texto, _TRUNCATE, formato, args);

        Guardia g(&cs);
        if (techo) return;

        if (!archivo)
        {
            wchar_t ruta[MAX_PATH + 12];
            Ruta(ruta, MAX_PATH + 12);
            archivo = CreateFileW(ruta, FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (archivo == INVALID_HANDLE_VALUE)
            {
                archivo = nullptr;
                techo = true;
                return;
            }
            LARGE_INTEGER tam = {};
            if (GetFileSizeEx(archivo, &tam)) bytes = static_cast<unsigned long long>(tam.QuadPart);
        }

        SYSTEMTIME st = {};
        GetLocalTime(&st);
        wchar_t linea[640];
        swprintf_s(linea, L"[%02u:%02u:%02u.%03u] %s\r\n",
            st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, texto);

        // UTF-16LE con BOM la primera vez (Notepad/VSCode lo interpretan solos)
        if (bytes == 0)
        {
            const wchar_t bom = L'\xFEFF';
            DWORD escritos = 0;
            if (WriteFile(archivo, &bom, sizeof(bom), &escritos, nullptr))
                bytes += escritos;
        }
        DWORD escritos = 0;
        const DWORD cbLinea = static_cast<DWORD>(wcslen(linea) * sizeof(wchar_t));
        if (WriteFile(archivo, linea, cbLinea, &escritos, nullptr))
        {
            bytes += escritos;
            if (bytes > (1ull << 20)) techo = true;   // techo 1 MB: dejar de escribir
        }
    }
};

static void LogDiagLinea(const wchar_t* formato, ...)
{
    va_list args;
    va_start(args, formato);
    LogDiag::Presente().LineaV(formato, args);
    va_end(args);
}

// Registra (hasta 200 veces) las interfaces que el huesped pide y no implementamos.
static void LogIid(const wchar_t* donde, REFIID r)
{
    static volatile LONG veces = 0;
    if (InterlockedIncrement(&veces) > 200) return;
    LogDiagLinea(L"QI no soportada en %s: {%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        donde, r.Data1, r.Data2, r.Data3,
        r.Data4[0], r.Data4[1], r.Data4[2], r.Data4[3],
        r.Data4[4], r.Data4[5], r.Data4[6], r.Data4[7]);
}


// ---------------------------------------------------------------------------
// FramePool â€” lector del archivo compartido (header + 2 slots + secuencia)
// Convencion de pc/Core/VirtualCameraPublisher.cs:
//   off 0: DWORD magia | 4: int version | 8: ancho | 12: alto |
//   16: tamanoFrame | 20: int slot | 24: LONG64 secuencia
//   datos: kCabecera + slot * kTamanoFrame
// ---------------------------------------------------------------------------

class FramePool
{
public:
    FramePool()
    {
        QueryPerformanceFrequency(&m_frecuencia);
    }

    // Copia el frame publicado mas nuevo (snapshot estable) o conserva el
    // anterior. Standby (negro) solo si nunca hubo frame bueno.
    bool CopiarUltimo(uint8_t* destino)
    {
        if (!AsegurarAbierto())
            return false;

        // Validar formato ANTES de copiar (defensa ante cambios del editor).
        const int ancho = *reinterpret_cast<volatile int*>(m_vista + 8);
        const int alto = *reinterpret_cast<volatile int*>(m_vista + 12);
        const int tamano = *reinterpret_cast<volatile int*>(m_vista + 16);
        if (ancho != kAncho || alto != kAlto || tamano != kTamanoFrame)
        {
            Cerrar();
            return false;
        }

        for (int intento = 0; intento < 3; ++intento)
        {
            const LONG64 antes = *reinterpret_cast<volatile LONG64*>(m_vista + 24);
            const int slot = *reinterpret_cast<volatile int*>(m_vista + 20);
            if (antes <= 0 || slot < 0 || slot > 1)
                return false;   // header recien inicializado: standby

            const uint8_t* origen = m_vista + kCabecera + slot * kTamanoFrame;
            memcpy(destino, origen, kTamanoFrame);

            const LONG64 despues = *reinterpret_cast<volatile LONG64*>(m_vista + 24);
            if (antes == despues)
                return true;   // snapshot limpio
        }
        return false;   // siempre en carrera: conservar frame previo
    }

private:
    bool AsegurarAbierto()
    {
        if (m_vista) return true;

        LARGE_INTEGER ahora;
        QueryPerformanceCounter(&ahora);
        // Maximo 1 intento de apertura por segundo.
        if (ahora.QuadPart < m_proximoIntento.QuadPart)
            return false;
        m_proximoIntento.QuadPart = ahora.QuadPart + m_frecuencia.QuadPart;

        wchar_t ruta[MAX_PATH * 2];
        if (FAILED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, 0, ruta)))
            return false;
        size_t largo = wcslen(ruta);
        wcscat_s(ruta, L"\\Lenscord\\frames.nv12");
        (void)largo;

        HANDLE archivo = CreateFileW(ruta, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (archivo == INVALID_HANDLE_VALUE)
            return false;

        HANDLE mapa = CreateFileMappingW(archivo, nullptr, PAGE_READONLY, 0, 0, nullptr);
        CloseHandle(archivo);
        if (!mapa) return false;

        uint8_t* vista = static_cast<uint8_t*>(MapViewOfFile(mapa, FILE_MAP_READ, 0, 0, 0));
        if (!vista) { CloseHandle(mapa); return false; }

        const UINT magia = *reinterpret_cast<volatile UINT*>(vista);
        const int version = *reinterpret_cast<volatile int*>(vista + 4);
        if (magia != kMagia || version != 1)
        {
            UnmapViewOfFile(vista);
            CloseHandle(mapa);
            return false;
        }

        m_mapa = mapa;
        m_vista = vista;
        return true;
    }

    void Cerrar()
    {
        if (m_vista) { UnmapViewOfFile(m_vista); m_vista = nullptr; }
        if (m_mapa) { CloseHandle(m_mapa); m_mapa = nullptr; }
    }

    HANDLE m_mapa = nullptr;
    uint8_t* m_vista = nullptr;
    LARGE_INTEGER m_proximoIntento = {};
    LARGE_INTEGER m_frecuencia = {};
};
// Frame NV12 de espera (negro 16/128) mientras no haya sesion activa.
static void LlenarStandbyNV12(uint8_t* destino)
{
    memset(destino, 16, kAncho * kAlto);
    memset(destino + kAncho * kAlto, 128, kAncho * kAlto / 2);
    // Distinguible de "camara muerta": banda que marca standby.
    for (int y = kAlto / 3; y < kAlto * 2 / 3; ++y)
        memset(destino + y * kAncho + kAncho / 3, 128, kAncho / 3);
}

// ---------------------------------------------------------------------------
// Conversion NV12 -> I420 / RGB24 / RGB32 con nearest-neighbor sobre el frame
// fuente 1280x720. Constantes BT.601 identicas a virtualcam/Source para que el
// color de ambas camaras coincida (298/516/409/100/208).
// ---------------------------------------------------------------------------
static inline uint8_t Clamp255(int v)
{
    return v < 0 ? 0 : (v > 255 ? static_cast<uint8_t>(255) : static_cast<uint8_t>(v));
}

static void ConvertirNV12(const uint8_t* nv12, uint8_t* destino, const GUID& tipo, int w, int h)
{
    const uint8_t* yPlan = nv12;
    const uint8_t* uvPlan = nv12 + kAncho * kAlto;

    if (tipo == MEDIASUBTYPE_I420_LEN)
    {
        // Plano Y
        for (int y = 0; y < h; ++y)
        {
            const uint8_t* fila = yPlan + (y * kAlto / h) * kAncho;
            uint8_t* d = destino + y * w;
            for (int x = 0; x < w; ++x)
                d[x] = fila[x * kAncho / w];
        }
        // Planos U y V desentrelazados del chroma NV12
        uint8_t* u = destino + w * h;
        uint8_t* v = u + (w / 2) * (h / 2);
        for (int y = 0; y < h / 2; ++y)
        {
            const int sy = (2 * y) * kAlto / h;
            const int suv = (sy / 2) * kAncho;
            const uint8_t* fila = uvPlan + suv;
            uint8_t* du = u + y * (w / 2);
            uint8_t* dv = v + y * (w / 2);
            for (int x = 0; x < w / 2; ++x)
            {
                const int sx = (2 * x) * kAncho / w;
                du[x] = fila[sx];
                dv[x] = fila[sx + 1];
            }
        }
        return;
    }

    const bool con24 = (tipo == MEDIASUBTYPE_RGB24);
    const int paso = con24 ? 3 : 4;
    for (int y = 0; y < h; ++y)
    {
        const int sy = y * kAlto / h;
        const uint8_t* filaY = yPlan + sy * kAncho;
        const uint8_t* filaUv = uvPlan + (sy / 2) * kAncho;
        uint8_t* cur = destino + static_cast<size_t>(h - 1 - y) * w * paso;   // BI_RGB con biHeight>0 es bottom-up
        for (int x = 0; x < w; ++x)
        {
            const int sx = x * kAncho / w;
            const int sxUv = sx & ~1;
            const int c = 298 * (filaY[sx] - 16);
            const int u = filaUv[sxUv] - 128;
            const int v = filaUv[sxUv + 1] - 128;
            *cur++ = Clamp255((c + 516 * u + 128) >> 8);      // B
            *cur++ = Clamp255((c - 100 * u - 208 * v + 128) >> 8);   // G
            *cur++ = Clamp255((c + 409 * v + 128) >> 8);      // R
            if (!con24) *cur++ = 255;                          // A
        }
    }
}

// ---------------------------------------------------------------------------
// Declaciones adelantadas
// ---------------------------------------------------------------------------
class LenscordFilter;
class LenscordPin;
class MemAlloc;
class LenscordSample;
class EnumMediaTipos;
class EnumPinosL;

// ---------------------------------------------------------------------------
// LenscordSample â€” IMediaSample duenada por MemAlloc
// (Release definida despues de MemAlloc porque llama a Devolver)
// ---------------------------------------------------------------------------
class LenscordSample : public IMediaSample
{
public:
    LenscordSample(MemAlloc* activador, uint8_t* memoria, int capacidad)
        : m_activador(activador), m_memoria(memoria), m_capacidad(capacidad)
    {
        InterlockedIncrement(&g_objetosVivos);
        m_actual = m_memoria;
    }
    ~LenscordSample() { InterlockedDecrement(&g_objetosVivos); }

    void Resetear()
    {
        m_len = 0;
        m_tIni = m_tFin = 0;
        m_tiempoOk = false;
        m_sync = false;
        m_discont = false;
        m_preroll = false;
    }

    void MarcarHuerfano() { m_activador = nullptr; }

    // --- IUnknown ----------------------------------------------------------
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown)
            *ppv = static_cast<IUnknown*>(this);
        else if (riid == IID_IMediaSample)
            *ppv = static_cast<IMediaSample*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    STDMETHODIMP_(ULONG) Release() override;   // definida despues de MemAlloc

    // --- IMediaSample ------------------------------------------------------
    STDMETHODIMP GetPointer(BYTE** ppBuffer) override
    {
        if (!ppBuffer) return E_POINTER;
        *ppBuffer = m_actual;
        return S_OK;
    }
    STDMETHODIMP_(long) GetSize() override { return m_capacidad; }
    STDMETHODIMP SetTime(REFERENCE_TIME* pIni, REFERENCE_TIME* pFin) override
    {
        if (pIni == nullptr || pFin == nullptr)
        {
            m_tiempoOk = false;
            m_tIni = m_tFin = 0;
            return S_FALSE;
        }
        m_tIni = *pIni;
        m_tFin = *pFin;
        m_tiempoOk = true;
        return S_OK;
    }
    STDMETHODIMP GetTime(REFERENCE_TIME* pIni, REFERENCE_TIME* pFin) override
    {
        if (!m_tiempoOk) return VFW_E_SAMPLE_TIME_NOT_SET;
        *pIni = m_tIni;
        *pFin = m_tFin;
        return S_OK;
    }
    STDMETHODIMP IsSyncPoint() override { return m_sync ? S_OK : S_FALSE; }
    STDMETHODIMP SetSyncPoint(BOOL b) override { m_sync = !!b; return S_OK; }
    STDMETHODIMP IsPreroll() override { return m_preroll ? S_OK : S_FALSE; }
    STDMETHODIMP SetPreroll(BOOL b) override { m_preroll = !!b; return S_OK; }
    STDMETHODIMP_(long) GetActualDataLength() override { return m_len; }
    STDMETHODIMP SetActualDataLength(long len) override
    {
        if (len < 0 || len > m_capacidad) return VFW_E_BUFFER_OVERFLOW;
        m_len = len;
        return S_OK;
    }
    STDMETHODIMP GetMediaType(AM_MEDIA_TYPE** pTipo) override
    {
        if (!pTipo) return E_POINTER;
        *pTipo = nullptr;   // formato fijo por conexion
        return S_FALSE;
    }
    STDMETHODIMP SetMediaType(AM_MEDIA_TYPE*) override { return S_OK; }
    STDMETHODIMP IsDiscontinuity() override { return m_discont ? S_OK : S_FALSE; }
    STDMETHODIMP SetDiscontinuity(BOOL b) override { m_discont = !!b; return S_OK; }
    STDMETHODIMP GetMediaTime(LONGLONG* pIni, LONGLONG* pFin) override
    {
        if (pIni) *pIni = 0;
        if (pFin) *pFin = 0;
        return S_FALSE;
    }
    STDMETHODIMP SetMediaTime(LONGLONG*, LONGLONG*) override { return S_OK; }

private:
    friend class MemAlloc;
    LONG m_refs = 0;
    MemAlloc* m_activador;
    uint8_t* m_memoria;
    uint8_t* m_actual;
    int m_capacidad;
    long m_len = 0;
    REFERENCE_TIME m_tIni = 0, m_tFin = 0;
    bool m_tiempoOk = false;
    bool m_sync = false, m_discont = false, m_preroll = false;
};

// ---------------------------------------------------------------------------
// MemAlloc â€” IMemAllocator propio
//
// Duenio de los samples (los borra en el dtor). Refcount del sample indica
// cuantos consumidores/produccion lo retienen; al llegar a 0 vuelve al pool
// via Devolver(). Decommit espera a que no queden prestados.
// ---------------------------------------------------------------------------
class MemAlloc : public IMemAllocator
{
public:
    MemAlloc()
    {
        InterlockedIncrement(&g_objetosVivos);
        m_evLibre = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    }
    ~MemAlloc()
    {
        DescomprometerInterno();
        if (m_evLibre) CloseHandle(m_evLibre);
        InterlockedDecrement(&g_objetosVivos);
    }

    // Config antes de Commit.
    HRESULT Preparar(int bytesPorBuffer, int buffers)
    {
        if (bytesPorBuffer <= 0 || buffers < 1) return E_INVALIDARG;
        Guardia g(&m_cs);
        m_bytesMin = bytesPorBuffer;
        m_buffersMin = buffers;
        return S_OK;
    }

    // --- IUnknown ----------------------------------------------------------
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown)
            *ppv = static_cast<IUnknown*>(this);
        else if (riid == IID_IMemAllocator)
            *ppv = static_cast<IMemAllocator*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const ULONG r = InterlockedDecrement(&m_refs);
        if (r == 0) delete this;
        return r;
    }

    // --- IMemAllocator -----------------------------------------------------
    STDMETHODIMP SetProperties(ALLOCATOR_PROPERTIES* pedido, ALLOCATOR_PROPERTIES* real) override
    {
        if (!pedido || !real) return E_POINTER;
        Guardia g(&m_cs);
        if (m_comprometido) return VFW_E_ALREADY_COMMITTED;
        if (pedido->cBuffers < 1 || pedido->cbBuffer < 1) return E_INVALIDARG;
        if (pedido->cbBuffer < m_bytesMin) pedido->cbBuffer = m_bytesMin;
        if (pedido->cBuffers < m_buffersMin) pedido->cBuffers = m_buffersMin;
        m_props = *pedido;
        *real = m_props;
        return S_OK;
    }

    STDMETHODIMP GetProperties(ALLOCATOR_PROPERTIES* real) override
    {
        if (!real) return E_POINTER;
        Guardia g(&m_cs);
        *real = m_props;
        return S_OK;
    }

    STDMETHODIMP Commit() override
    {
        Guardia g(&m_cs);
        if (m_comprometido) return S_OK;
        if (m_props.cbBuffer <= 0 || m_props.cBuffers <= 0)
        {
            // nadie llamo SetProperties: usar defectos propios
            m_props.cbBuffer = m_bytesMin;
            m_props.cBuffers = m_buffersMin;
            m_props.cbAlign = 1;
        }
        const size_t bloque = static_cast<size_t>(m_props.cbBuffer) * static_cast<size_t>(m_props.cBuffers);
        m_bloque = static_cast<uint8_t*>(malloc(bloque));
        if (!m_bloque) return E_OUTOFMEMORY;
        for (int i = 0; i < m_props.cBuffers; ++i)
        {
            LenscordSample* s = new (std::nothrow) LenscordSample(this,
                m_bloque + static_cast<size_t>(i) * m_props.cbBuffer, m_props.cbBuffer);
            if (!s) { Free(); return E_OUTOFMEMORY; }
            m_todos.push_back(s);
            s->Resetear();
            m_libres.push_back(s);
        }
        m_comprometido = true;
        SetEvent(m_evLibre);
        return S_OK;
    }

    STDMETHODIMP Decommit() override { DescomprometerInterno(); return S_OK; }

    STDMETHODIMP GetBuffer(IMediaSample** ppBuffer, REFERENCE_TIME*, REFERENCE_TIME*, DWORD) override
    {
        if (!ppBuffer) return E_POINTER;
        *ppBuffer = nullptr;
        LenscordSample* s = nullptr;
        for (;;)
        {
            {
                Guardia g(&m_cs);
                if (!m_comprometido) return VFW_E_NOT_COMMITTED;
                if (!m_libres.empty())
                {
                    s = m_libres[m_libres.size() - 1];
                    m_libres.pop_back();
                    s->AddRef();
                    ++m_prestados;
                    break;
                }
            }
            if (WaitForSingleObject(m_evLibre, 1000) != WAIT_OBJECT_0)
            {
                Guardia g(&m_cs);
                if (!m_comprometido) return VFW_E_NOT_COMMITTED;
            }
        }
        *ppBuffer = s;   // entregar la referencia (falto esto: 0 entregas)
        return S_OK;
    }

    STDMETHODIMP ReleaseBuffer(IMediaSample*) override { return S_OK; }  // via Devolver()

    void Devolver(LenscordSample* s)
    {
        Guardia g(&m_cs);
        --m_prestados;
        s->Resetear();
        m_libres.push_back(s);
        SetEvent(m_evLibre);
    }

    int BytesMin() const { return m_bytesMin; }

private:
    void DescomprometerInterno()
    {
        // Marcar no-comprometido y dar a los samples prestados hasta ~3 s para
        // regresar. Si el hilo de entrega murio con un sample en la mano,
        // procedemos igual: se pierde ese sample, no el proceso.
        for (int ronda = 0; ronda < 30; ++ronda)
        {
            {
                Guardia g(&m_cs);
                m_comprometido = false;
                if (m_prestados <= 0) break;
            }
            WaitForSingleObject(m_evLibre, 100);
        }
        Guardia g(&m_cs);
        m_prestados = 0;
        Free();
        // m_props se conserva: un Commit posterior reusa las propiedades ya
        // negociadas, incluyendo el margen hasta kTamanoSampleMax (hallazgo 4).
        SetEvent(m_evLibre);
    }

    void Free()
    {
        for (size_t i = 0; i < m_todos.size(); ++i) { m_todos[i]->MarcarHuerfano(); delete m_todos[i]; }
        m_todos.clear();
        m_libres.clear();
        if (m_bloque) { free(m_bloque); m_bloque = nullptr; }
        m_prestados = 0;
    }

    friend class LenscordSample;

    Seccion m_cs;
    LONG m_refs = 1;
    HANDLE m_evLibre = nullptr;
    ALLOCATOR_PROPERTIES m_props;
    std::vector<LenscordSample*> m_todos;
    std::vector<LenscordSample*> m_libres;
    uint8_t* m_bloque = nullptr;
    long m_prestados = 0;
    int m_bytesMin = kTamanoFrame;
    int m_buffersMin = 3;
    bool m_comprometido = false;
};

// La Release del sample necesita MemAlloc completo (Devolver), pues vive fuera.
STDMETHODIMP_(ULONG) LenscordSample::Release()
{
    const ULONG r = InterlockedDecrement(&m_refs);
    if (r == 0)
    {
        // La memoria vive en el activador, no en el sample.
        if (m_activador) m_activador->Devolver(this);
        else delete this;   // huerfano post-Decommit
    }
    return r;
}

// ---------------------------------------------------------------------------
// Formatos ofrecidos
// ---------------------------------------------------------------------------
static const struct { int w; int h; } g_tamanos[] =
{
    { 1280, 720 },
    { 640, 480 },
    { 320, 240 },
    { 160, 120 },
};
static const int kTamanos = sizeof(g_tamanos) / sizeof(g_tamanos[0]);
static const int kFormatos = 3;   // I420, RGB24, RGB32

static int BytesDeFormato(const GUID& tipo, int w, int h)
{
    if (tipo == MEDIASUBTYPE_I420_LEN) return w * h * 3 / 2;
    if (tipo == MEDIASUBTYPE_RGB24) return w * h * 3;
    if (tipo == MEDIASUBTYPE_RGB32) return w * h * 4;
    return 0;
}

static bool EsSubtipoSoportado(const GUID& tipo)
{
    return tipo == MEDIASUBTYPE_I420_LEN || tipo == MEDIASUBTYPE_RGB24 || tipo == MEDIASUBTYPE_RGB32;
}

static const wchar_t* SubTipoNombre(const GUID& tipo)
{
    if (tipo == MEDIASUBTYPE_I420_LEN) return L"I420";
    if (tipo == MEDIASUBTYPE_RGB24) return L"RGB24";
    if (tipo == MEDIASUBTYPE_RGB32) return L"RGB32";
    return L"otro";
}

static const GUID& SubtipoPorIndice(int f)
{
    static const GUID* tabla[3] = { gpSubI420, &MEDIASUBTYPE_RGB24, &MEDIASUBTYPE_RGB32 };
    return *tabla[f];
}

// ---------------------------------------------------------------------------
// LenscordPin â€” pin de salida (IPin + IAMStreamConfig + IKsPropertySet)
// ---------------------------------------------------------------------------
class LenscordPin : public IPin, public IAMStreamConfig, public IKsPropertySet
{
public:
    explicit LenscordPin(LenscordFilter* filtro) : m_filtro(filtro) {}
    ~LenscordPin() { Limpiar(); }

    // El refcount se delega al filtro (semantica de base classes): mientras
    // exista una referencia externa al pin (o al filtro) el filtro vive.
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    STDMETHODIMP Connect(IPin* pinReceptor, const AM_MEDIA_TYPE* mt) override;
    STDMETHODIMP ReceiveConnection(IPin* conector, const AM_MEDIA_TYPE* mt) override;
    STDMETHODIMP Disconnect() override;
    STDMETHODIMP ConnectedTo(IPin** pin) override;
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* mt) override;
    STDMETHODIMP QueryPinInfo(PIN_INFO* info) override;
    STDMETHODIMP QueryDirection(PIN_DIRECTION* dir) override;
    STDMETHODIMP QueryId(LPWSTR* id) override;
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* mt) override;
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes** enumTipos) override;
    STDMETHODIMP QueryInternalConnections(IPin** apin, ULONG* n) override;
    STDMETHODIMP EndOfStream() override { return E_UNEXPECTED; }
    STDMETHODIMP BeginFlush() override { return E_UNEXPECTED; }
    STDMETHODIMP EndFlush() override { return E_UNEXPECTED; }
    STDMETHODIMP NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) override { return S_OK; }

    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* mt) override;
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** mt) override;
    STDMETHODIMP GetNumberOfCapabilities(int* cuenta, int* tamanioCaps) override;
    STDMETHODIMP GetStreamCaps(int i, AM_MEDIA_TYPE** mt, BYTE* caps) override;

    STDMETHODIMP Set(REFGUID propSet, DWORD propId, void* datosInstancia, DWORD cbInstancia,
        void* datosProp, DWORD cbProp) override;
    STDMETHODIMP Get(REFGUID propSet, DWORD propId, void* datosInstancia, DWORD cbInstancia,
        void* datosProp, DWORD cbProp, DWORD* cbDevuelto) override;
    STDMETHODIMP QuerySupported(REFGUID propSet, DWORD propId, DWORD* soporte) override;

    // ciclo de vida
    HRESULT ConectarInterno(IPin* pinReceptor, const AM_MEDIA_TYPE* mt);
    void DesconectarInterno();
    HRESULT IniciarHilo();
    bool PararHilo();   // false: el hilo quedo zombi (no se mata, se loguea)
    void Limpiar();
    HRESULT CopiarFormato(int indice, AM_MEDIA_TYPE** mt, BYTE* caps);   // GetStreamCaps/Enum
    bool HayConexion() const { return m_pinEntrada != nullptr; }
    bool HayHiloColgado() const { return m_hiloColgado != nullptr; }

private:
    friend class LenscordFilter;
    friend class EnumMediaTipos;
    friend class EnumPinosL;

    HRESULT DecideAllocator();
    bool EsFormatoValido(const AM_MEDIA_TYPE* mt);
    void GuardarTipoConectado(const AM_MEDIA_TYPE* mt);
    HRESULT TiempoActualStream(REFERENCE_TIME* t);

    LenscordFilter* m_filtro;
    IPin* m_pinEntrada = nullptr;
    IMemInputPin* m_entrada = nullptr;
    IMemAllocator* m_allocador = nullptr;   // propio (MemAlloc) o del sink
    AM_MEDIA_TYPE m_mtConectado = {};

    AM_MEDIA_TYPE m_mtSeleccionado = {};   // SetFormat previo a conectar

    // datos derivados del ultimo SetFormat: se acepta TAMBIEN despues de
    // conectar porque libwebrtc/Discord conecta primero y pide formato
    // despues (los drivers virtuales deben emitir lo pedido).
    bool m_pendiente = false;
    GUID m_subtipoPendiente = {};
    int m_wPendiente = kAncho;
    int m_hPendiente = kAlto;

    // datos derivados del formato conectado (escritos una vez al conectar)
    GUID m_subtipoConectado = {};
    int m_wConectado = kAncho;
    int m_hConectado = kAlto;

    HANDLE m_hilo = nullptr;
    HANDLE m_evSalir = nullptr;
    volatile LONG m_runFlag = 0;
    HANDLE m_hiloColgado = nullptr;     // hilo zombi: PararHilo no pudo detenerlo (no se mata)
    LONG m_framesEntregados = 0;        // log: los primeros 5 frames, luego 1 de cada 300
    LONG m_fallosGetBuf = 0;            // log: primera vez y cada 100 fallos de GetBuffer
    LONG m_fallosReceive = 0;           // log: primera vez y cada 100 fallos de Receive
    LONGLONG m_baseQpc = 0;            // anclaje del tiempo de stream sintetico
    bool m_tengoPrimerFrame = false;

    // snapshot del publisher
    FramePool m_pool;
    std::vector<uint8_t> m_frameNV12;   // ultimo frame bueno
    bool m_tengoFrame = false;
    REFERENCE_TIME m_tProximoStream = 0;

    static DWORD WINAPI HiloEntrega(LPVOID param);
    void BucleEntrega();
};

// ---------------------------------------------------------------------------
// EnumMediaTipos â€” IEnumMediaTypes
// ---------------------------------------------------------------------------
class EnumMediaTipos : public IEnumMediaTypes
{
public:
    EnumMediaTipos(LenscordPin* pin, int indice) : m_pin(pin), m_i(indice) {}
    ~EnumMediaTipos() = default;

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown)
            *ppv = static_cast<IUnknown*>(this);
        else if (riid == IID_IEnumMediaTypes)
            *ppv = static_cast<IEnumMediaTypes*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const ULONG r = InterlockedDecrement(&m_refs);
        if (r == 0) delete this;
        return r;
    }

    STDMETHODIMP Next(ULONG cTipos, AM_MEDIA_TYPE** tipos, ULONG* obtenidos) override
    {
        if (!tipos) return E_POINTER;
        ULONG n = 0;
        while (n < cTipos && m_pin->CopiarFormato(m_i, &tipos[n], nullptr) == S_OK)
        {
            ++m_i;
            ++n;
        }
        if (obtenidos) *obtenidos = n;
        return (n == cTipos) ? S_OK : ((n == 0) ? S_FALSE : S_FALSE);
    }
    STDMETHODIMP Skip(ULONG cTipos) override
    {
        m_i += static_cast<int>(cTipos);
        return (m_i < kTamanos * kFormatos) ? S_OK : S_FALSE;
    }
    STDMETHODIMP Reset() override { m_i = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumMediaTypes** copia) override
    {
        if (!copia) return E_POINTER;
        EnumMediaTipos* e = new (std::nothrow) EnumMediaTipos(m_pin, m_i);
        if (!e) return E_OUTOFMEMORY;
        *copia = e;
        return S_OK;
    }

private:
    LONG m_refs = 1;
    LenscordPin* m_pin;
    int m_i;
};

// ---------------------------------------------------------------------------
// EnumPinosL â€” IEnumPins (un pin)
// ---------------------------------------------------------------------------
class EnumPinosL : public IEnumPins
{
public:
    EnumPinosL(LenscordFilter* filtro, int indice) : m_filtro(filtro), m_i(indice) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown)
            *ppv = static_cast<IUnknown*>(this);
        else if (riid == IID_IEnumPins)
            *ppv = static_cast<IEnumPins*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const ULONG r = InterlockedDecrement(&m_refs);
        if (r == 0) delete this;
        return r;
    }

    STDMETHODIMP Next(ULONG cPines, IPin** pines, ULONG* obtenidos) override;   // definida tras LenscordFilter
    STDMETHODIMP Skip(ULONG cPines) override
    {
        m_i += static_cast<int>(cPines);
        return m_i < 1 ? S_OK : S_FALSE;
    }
    STDMETHODIMP Reset() override { m_i = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumPins** copia) override
    {
        if (!copia) return E_POINTER;
        EnumPinosL* e = new (std::nothrow) EnumPinosL(m_filtro, m_i);
        if (!e) return E_OUTOFMEMORY;
        *copia = e;
        return S_OK;
    }

private:
    LONG m_refs = 1;
    LenscordFilter* m_filtro;
    int m_i;
};

// ---------------------------------------------------------------------------
// LenscordFilter â€” IBaseFilter + IPersist + IAMStreamConfig (delega al pin)
// ---------------------------------------------------------------------------
// IBaseFilter ya hereda IPersist (evitamos la base redundante)
class LenscordFilter : public IBaseFilter, public IAMStreamConfig, public IPersistPropertyBag, public IAMFilterMiscFlags
{
public:
    LenscordFilter()
    {
        InterlockedIncrement(&g_objetosVivos);
        m_pin = new (std::nothrow) LenscordPin(this);
    }
    ~LenscordFilter()
    {
        if (m_pin)
        {
            // Hallazgo 3: sin TerminateThread. Si el hilo no sale en el plazo,
            // el objeto entero se filtra (no se borra) para que `this` siga
            // siendo valido mientras el worker exista; ademas no se decrementa
            // g_objetosVivos, con lo cual DllCanUnloadNow queda en S_FALSE
            // y la DLL sigue cargada hasta el fin del proceso.
            if (!m_pin->PararHilo() || m_pin->HayHiloColgado())
                return;
            m_pin->DesconectarInterno();
            delete m_pin;
            m_pin = nullptr;
        }
        if (m_reloj) { m_reloj->Release(); m_reloj = nullptr; }
        if (m_grafo) { m_grafo->Release(); m_grafo = nullptr; }
        InterlockedDecrement(&g_objetosVivos);
    }

    LenscordPin* ElPin() { return m_pin; }
    STDMETHODIMP_(ULONG) AddRefExterno() { return InterlockedIncrement(&m_refs); }

    // --- IUnknown ----------------------------------------------------------
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown)
            *ppv = static_cast<IUnknown*>(static_cast<IBaseFilter*>(this));
        else if (riid == IID_IMediaFilter)
            *ppv = static_cast<IMediaFilter*>(this);
        else if (riid == IID_IPersist)
            *ppv = static_cast<IPersist*>(static_cast<IBaseFilter*>(this));
        else if (riid == IID_IBaseFilter)
            *ppv = static_cast<IBaseFilter*>(this);
        else if (riid == IID_IAMStreamConfig)
            *ppv = static_cast<IAMStreamConfig*>(this);
        else if (riid == IID_IPersistPropertyBag)
            *ppv = static_cast<IPersistPropertyBag*>(this);
        else if (riid == IID_IAMFilterMiscFlags)
            *ppv = static_cast<IAMFilterMiscFlags*>(this);
        else { LogIid(L"filtro", riid); *ppv = nullptr; return E_NOINTERFACE; }
        AddRefExterno();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return AddRefExterno(); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const ULONG r = InterlockedDecrement(&m_refs);
        if (r == 0)
        {
            // Fuga intencional si quedo un hilo zombi: el worker aun puede tocar
            // m_filtro->m_cs, y un `return` en el destructor no evita que la
            // memoria se libere ni que se destruyan los miembros.
            if (m_pin && m_pin->HayHiloColgado()) return 0;
            delete this;
        }
        return r;
    }

    // --- IPersist ----------------------------------------------------------
    STDMETHODIMP GetClassID(CLSID* clsid) override
    {
        if (!clsid) return E_POINTER;
        *clsid = CLSID_LenscordDShow;
        return S_OK;
    }

// --- IPersistPropertyBag ---------------------------------------------------
    STDMETHODIMP InitNew() override { return S_OK; }
    STDMETHODIMP Load(IPropertyBag* pPropBag, IErrorLog* pErrorLog) override
    {
        (void)pPropBag; (void)pErrorLog;
        return S_OK;
    }
    STDMETHODIMP Save(IPropertyBag* pPropBag, BOOL fClearDirty, BOOL fSaveAllProperties) override
    {
        (void)pPropBag; (void)fClearDirty; (void)fSaveAllProperties;
        return S_OK;
    }

// --- IAMFilterMiscFlags --------------------------------------------------------
    STDMETHODIMP_(ULONG) GetMiscFlags() override
    {
        return AM_FILTER_MISC_FLAGS_IS_SOURCE;
    }

    // --- IMediaFilter ------------------------------------------------------
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Run(REFERENCE_TIME t) override;
    STDMETHODIMP GetState(DWORD msTimeout, FILTER_STATE* st) override
    {
        (void)msTimeout;
        if (!st) return E_POINTER;
        Guardia g(&m_cs);
        *st = static_cast<FILTER_STATE>(m_estado);
        return S_OK;
    }
    STDMETHODIMP SetSyncSource(IReferenceClock* reloj) override
    {
        Guardia g(&m_cs);
        if (m_reloj) m_reloj->Release();
        m_reloj = reloj;
        if (m_reloj) m_reloj->AddRef();
        return S_OK;
    }
    STDMETHODIMP GetSyncSource(IReferenceClock** reloj) override
    {
        if (!reloj) return E_POINTER;
        Guardia g(&m_cs);
        *reloj = m_reloj;
        if (*reloj) (*reloj)->AddRef();
        return S_OK;
    }

    // --- IBaseFilter -------------------------------------------------------
    STDMETHODIMP EnumPins(IEnumPins** enumPins) override
    {
        if (!enumPins) return E_POINTER;
        EnumPinosL* e = new (std::nothrow) EnumPinosL(this, 0);
        if (!e) return E_OUTOFMEMORY;
        *enumPins = e;
        return S_OK;
    }
    STDMETHODIMP FindPin(LPCWSTR id, IPin** pin) override
    {
        if (!id || !pin) return E_POINTER;
        *pin = nullptr;
        if (m_pin && wcscmp(id, L"Lenscord") == 0)
        {
            *pin = m_pin;
            (*pin)->AddRef();
            return S_OK;
        }
        return VFW_E_NOT_FOUND;
    }
    STDMETHODIMP QueryFilterInfo(FILTER_INFO* info) override
    {
        if (!info) return E_POINTER;
        wcscpy_s(info->achName, m_nombre);
        info->pGraph = m_grafo;
        if (info->pGraph) info->pGraph->AddRef();
        return S_OK;
    }
    STDMETHODIMP JoinFilterGraph(IFilterGraph* grafo, LPCWSTR nombre) override
    {
        Guardia g(&m_cs);
        if (m_grafo) { m_grafo->Release(); m_grafo = nullptr; }
        m_grafo = grafo;
        if (m_grafo) m_grafo->AddRef();
        if (nombre && nombre[0]) wcscpy_s(m_nombre, nombre);
        return S_OK;
    }
    STDMETHODIMP QueryVendorInfo(LPWSTR* vendor) override
    {
        if (!vendor) return E_POINTER;
        const wchar_t* v = L"Lenscord";
        const size_t bytes = (wcslen(v) + 1) * sizeof(wchar_t);
        *vendor = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
        if (!*vendor) return E_OUTOFMEMORY;
        memcpy(*vendor, v, bytes);
        return S_OK;
    }

    // --- IAMStreamConfig (delega al pin) -----------------------------------
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* mt) override { return m_pin ? m_pin->SetFormat(mt) : E_FAIL; }
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** mt) override { return m_pin ? m_pin->GetFormat(mt) : E_FAIL; }
    STDMETHODIMP GetNumberOfCapabilities(int* c, int* t) override { return m_pin ? m_pin->GetNumberOfCapabilities(c, t) : E_FAIL; }
    STDMETHODIMP GetStreamCaps(int i, AM_MEDIA_TYPE** mt, BYTE* caps) override { return m_pin ? m_pin->GetStreamCaps(i, mt, caps) : E_FAIL; }

    enum class EstadoFiltro { Parado = 0, Pausa = 1, Corriendo = 2 };

    Seccion m_cs;
    LenscordPin* m_pin = nullptr;
    IFilterGraph* m_grafo = nullptr;
    wchar_t m_nombre[128] = L"Lenscord";
    IReferenceClock* m_reloj = nullptr;
    EstadoFiltro m_estado = EstadoFiltro::Parado;
    REFERENCE_TIME m_tInicioRun = 0;

private:
    HRESULT EstadoInt(EstadoFiltro nuevo);

    LONG m_refs = 0;
};

STDMETHODIMP EnumPinosL::Next(ULONG cPines, IPin** pines, ULONG* obtenidos)
{
    if (!pines) return E_POINTER;
    ULONG n = 0;
    while (n < cPines && m_i < 1)
    {
        pines[n++] = m_filtro->ElPin();
        m_filtro->ElPin()->AddRef();
        ++m_i;
    }
    if (obtenidos) *obtenidos = n;
    return (n == cPines && n > 0) ? S_OK : S_FALSE;
}

HRESULT LenscordFilter::EstadoInt(EstadoFiltro nuevo)
{
    LenscordPin* pin = nullptr;
    bool detener = false;
    HRESULT hr = S_OK;
    {
        Guardia g(&m_cs);
        if (m_estado == nuevo) return S_OK;
        if (nuevo == EstadoFiltro::Parado)
        {
            m_estado = EstadoFiltro::Parado;
            pin = m_pin;             // la espera del hilo se hace fuera del lock:
            detener = true;          // el worker necesita m_cs en cada vuelta
        }
        else
        {
            if (m_pin) hr = m_pin->IniciarHilo();
            m_estado = nuevo;
        }
    }
    if (detener && pin) pin->PararHilo();
    return hr;
}

STDMETHODIMP LenscordFilter::Stop() { return EstadoInt(EstadoFiltro::Parado); }

STDMETHODIMP LenscordFilter::Pause() { return EstadoInt(EstadoFiltro::Pausa); }

STDMETHODIMP LenscordFilter::Run(REFERENCE_TIME t)
{
    m_tInicioRun = t;
    return EstadoInt(EstadoFiltro::Corriendo);
}

// ---------------------------------------------------------------------------
// LenscordPin â€” implementacion
// ---------------------------------------------------------------------------

STDMETHODIMP LenscordPin::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    IUnknown* desconocido = nullptr;
    if (riid == IID_IUnknown)
        desconocido = static_cast<IUnknown*>(static_cast<IPin*>(this));
    else if (riid == IID_IPin)
        desconocido = static_cast<IPin*>(this);
    else if (riid == IID_IAMStreamConfig)
        desconocido = static_cast<IAMStreamConfig*>(this);
    else if (riid == IID_IKsPropertySet)
        desconocido = static_cast<IKsPropertySet*>(this);
else if (riid == IID_IKsPropertySetProxy)
        desconocido = static_cast<IKsPropertySet*>(this);   // ambas variantes
    else { LogIid(L"pin", riid); *ppv = nullptr; return E_NOINTERFACE; }

    *ppv = desconocido;
    m_filtro->AddRefExterno();
    return S_OK;
}

STDMETHODIMP_(ULONG) LenscordPin::AddRef()
{
    return m_filtro->AddRefExterno();
}

STDMETHODIMP_(ULONG) LenscordPin::Release()
{
    // El filtro puede destruirse (y a este pin dentro de la misma llamada):
    // guardar el puntero primero y no tocar miembros despues del Release.
    LenscordFilter* filtro = m_filtro;
    return filtro->Release();
}

// -- validacion de formatos -------------------------------------------------
// Extrae las dimensiones de un media type VIDEOINFOHEADER(-2). Para tipos
// parciales (sin pbFormat) usa el tamaño por defecto. Devuelve false si el
// formattype ni siquiera es VideoInfo(-2).
static bool DimensionesDelFormato(const AM_MEDIA_TYPE* mt, int* wym, int* hOut)
{
    *wym = kAncho;
    *hOut = kAlto;
    if (!mt) return false;
    if (mt->formattype == FORMAT_VideoInfo && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER))
    {
        auto* vih = reinterpret_cast<const VIDEOINFOHEADER*>(mt->pbFormat);
        *wym = abs(vih->bmiHeader.biWidth);
        *hOut = abs(vih->bmiHeader.biHeight);
        return true;
    }
    if (mt->formattype == FORMAT_VideoInfo2 && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER2))
    {
        auto* vih = reinterpret_cast<const VIDEOINFOHEADER2*>(mt->pbFormat);
        *wym = abs(vih->bmiHeader.biWidth);
        *hOut = abs(vih->bmiHeader.biHeight);
        return true;
    }
    if (mt->formattype == kGuidNulo || !mt->pbFormat)
        return true;   // parcial: dimensiones por defecto
    return false;
}

bool LenscordPin::EsFormatoValido(const AM_MEDIA_TYPE* mt)
{
    if (!mt) return false;
    if (mt->majortype != MEDIATYPE_Video) return false;
    if (!EsSubtipoSoportado(mt->subtype)) return false;

    int w = kAncho, h = kAlto;
    if (mt->formattype == FORMAT_VideoInfo && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER))
    {
        auto* vih = reinterpret_cast<const VIDEOINFOHEADER*>(mt->pbFormat);
        w = abs(vih->bmiHeader.biWidth);
        h = abs(vih->bmiHeader.biHeight);
    }
    else if (mt->formattype == FORMAT_VideoInfo2 && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER2))
    {
        auto* vih = reinterpret_cast<const VIDEOINFOHEADER2*>(mt->pbFormat);
        w = abs(vih->bmiHeader.biWidth);
        h = abs(vih->bmiHeader.biHeight);
    }
    else if (mt->formattype == kGuidNulo || !mt->pbFormat)
    {
        return true;   // parcial: dimensiones por defecto
    }
    else
    {
        return false;
    }

    // dimensiones razonables (dejen de escalar el NV12 de 1280x720)
    if (w < 32 || h < 24 || w > 1280 || h > 720) return false;
    return true;
}

void LenscordPin::GuardarTipoConectado(const AM_MEDIA_TYPE* mt)
{
    if (m_mtConectado.pbFormat) CoTaskMemFree(m_mtConectado.pbFormat);
    m_mtConectado = *mt;
    m_mtConectado.pUnk = nullptr;
    m_mtConectado.pbFormat = nullptr;
    if (mt->cbFormat > 0)
    {
        m_mtConectado.pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(mt->cbFormat));
        if (m_mtConectado.pbFormat)
            memcpy(m_mtConectado.pbFormat, mt->pbFormat, mt->cbFormat);
        else
            m_mtConectado.cbFormat = 0;
    }

    m_subtipoConectado = mt->subtype;
    m_wConectado = kAncho;
    m_hConectado = kAlto;
    if (mt->formattype == FORMAT_VideoInfo && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER))
    {
        auto* vih = reinterpret_cast<const VIDEOINFOHEADER*>(mt->pbFormat);
        m_wConectado = abs(vih->bmiHeader.biWidth);
        m_hConectado = abs(vih->bmiHeader.biHeight);
        return;
    }
    else if (mt->formattype == FORMAT_VideoInfo2 && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER2))
    {
        auto* vih = reinterpret_cast<const VIDEOINFOHEADER2*>(mt->pbFormat);
        m_wConectado = abs(vih->bmiHeader.biWidth);
        m_hConectado = abs(vih->bmiHeader.biHeight);
        return;
    }
    else if (mt->pbFormat && DimensionesDelFormato(mt, &m_wConectado, &m_hConectado))
    {
        return;
    }
}

// -- allocator --------------------------------------------------------------
HRESULT LenscordPin::DecideAllocator()
{
    if (!m_entrada) return E_UNEXPECTED;

    const int bytes = BytesDeFormato(m_subtipoConectado, m_wConectado, m_hConectado);
    if (bytes <= 0) return E_FAIL;

    ALLOCATOR_PROPERTIES requeridos = {};
    const HRESULT hrR = m_entrada->GetAllocatorRequirements(&requeridos);
    (void)hrR;

    ALLOCATOR_PROPERTIES pedido = {};
    pedido.cbBuffer = bytes;
    pedido.cBuffers = 3;
    pedido.cbAlign = 1;
    pedido.cbPrefix = 0;
    // margen: un SetFormat posterior a la conexion (libwebrtc/Discord) puede
    // pedir un subtipo o dimension mayor; el buffer cubre el maximo emitible.
    if (pedido.cbBuffer < kTamanoSampleMax) pedido.cbBuffer = kTamanoSampleMax;
    if (requeridos.cbBuffer > pedido.cbBuffer) pedido.cbBuffer = requeridos.cbBuffer;
    if (requeridos.cBuffers > pedido.cBuffers) pedido.cBuffers = requeridos.cBuffers;
    if (requeridos.cbAlign > pedido.cbAlign) pedido.cbAlign = requeridos.cbAlign;

    IMemAllocator* aloc = nullptr;
    HRESULT hr = m_entrada->GetAllocator(&aloc);
    if (SUCCEEDED(hr) && aloc)
    {
        ALLOCATOR_PROPERTIES real = {};
        ALLOCATOR_PROPERTIES intento = pedido;
        HRESULT hrP = aloc->SetProperties(&intento, &real);
        if (SUCCEEDED(hrP) && (LONG)real.cbBuffer >= pedido.cbBuffer)
        {
            hr = m_entrada->NotifyAllocator(aloc, FALSE);
            if (SUCCEEDED(hr))
            {
                m_allocador = aloc;   // transferimos la referencia obtenida
                LogDiagLinea(L"alloc elegido: del sink, cbBuffer=%ld cBuffers=%ld",
                    real.cbBuffer, real.cBuffers);
                return S_OK;
            }
        }
        aloc->Release();
        aloc = nullptr;
    }

    // Allocator propio
    MemAlloc* nuestro = new (std::nothrow) MemAlloc();
    if (!nuestro) return E_OUTOFMEMORY;
    nuestro->Preparar(bytes, 3);
    ALLOCATOR_PROPERTIES real = {};
    hr = nuestro->SetProperties(&pedido, &real);
    if (SUCCEEDED(hr)) hr = nuestro->Commit();
    if (SUCCEEDED(hr)) hr = m_entrada->NotifyAllocator(nuestro, FALSE);
    if (SUCCEEDED(hr))
    {
        m_allocador = nuestro;
        LogDiagLinea(L"alloc elegido: propio (MemAlloc), cbBuffer=%ld cBuffers=%ld",
            real.cbBuffer, real.cBuffers);
        return S_OK;
    }
    nuestro->Release();
    return (hr == S_OK) ? E_FAIL : hr;
}

// -- conexion ---------------------------------------------------------------
HRESULT LenscordPin::ConectarInterno(IPin* pinReceptor, const AM_MEDIA_TYPE* mt)
{
    if (!mt) return E_POINTER;
    {
        Guardia g(&m_filtro->m_cs);
        if (m_entrada) { LogDiagLinea(L"connect: ya conectado"); return VFW_E_ALREADY_CONNECTED; }
        if (m_filtro->m_estado != LenscordFilter::EstadoFiltro::Parado)
        { LogDiagLinea(L"connect: no parado estado=%d", (int)m_filtro->m_estado); return VFW_E_NOT_STOPPED; }
        if (!EsFormatoValido(mt))
        { LogDiagLinea(L"connect: formato invalido"); return VFW_E_TYPE_NOT_ACCEPTED; }
    }

    // El par debe aceptar el tipo primero
    HRESULT hr = pinReceptor->ReceiveConnection(this, mt);
    if (FAILED(hr)) { LogDiagLinea(L"connect: ReceiveConnection fallo 0x%08X", hr); return hr; }

    Guardia g(&m_filtro->m_cs);
    if (m_entrada) return VFW_E_ALREADY_CONNECTED;

    hr = pinReceptor->QueryInterface(IID_IMemInputPin, reinterpret_cast<void**>(&m_entrada));
    if (FAILED(hr) || !m_entrada)
    {
        m_entrada = nullptr;
        pinReceptor->Disconnect();
        LogDiagLinea(L"connect: IMemInputPin fallo 0x%08X", hr);
        return VFW_E_NO_ACCEPTABLE_TYPES;
    }

    m_pinEntrada = pinReceptor;
    m_pinEntrada->AddRef();
    GuardarTipoConectado(mt);

    hr = DecideAllocator();
    if (FAILED(hr))
    {
        if (m_entrada) { m_entrada->Release(); m_entrada = nullptr; }
        if (m_pinEntrada) { m_pinEntrada->Release(); m_pinEntrada = nullptr; }
        if (m_mtConectado.pbFormat) { CoTaskMemFree(m_mtConectado.pbFormat); }
        ZeroMemory(&m_mtConectado, sizeof(m_mtConectado));
        pinReceptor->Disconnect();
        LogDiagLinea(L"connect: DecideAllocator fallo 0x%08X", hr);
        return FAILED(hr) ? hr : E_FAIL;
    }
    // lo conectado define el formato de ahora en adelante
    m_pendiente = false;
    LogDiagLinea(L"connect: %s %dx%d", SubTipoNombre(m_subtipoConectado), m_wConectado, m_hConectado);
    return S_OK;
}

STDMETHODIMP LenscordPin::Connect(IPin* pinReceptor, const AM_MEDIA_TYPE* mt)
{
    LogDiagLinea(L"Connect llamado (propuesta=%s)", (mt && mt->majortype != kGuidNulo) ? L"si" : L"no");
    if (!pinReceptor) return E_POINTER;

    const bool hayPropuesta = (mt != nullptr && mt->majortype != kGuidNulo);
    if (hayPropuesta && mt->subtype != kGuidNulo)
    {
        // propuesta concreta de tipo (dimensiones flexibles)
        return ConectarInterno(pinReceptor, mt);
    }

    // Un SetFormat previo tiene prioridad (asi trabaja libwebrtc/Discord:
    // primero SetFormat y despues RenderStream sin tipo).
    if (!hayPropuesta && m_pendiente && m_mtSeleccionado.pbFormat)
    {
        AM_MEDIA_TYPE copia = m_mtSeleccionado;   // copia superficial (vida del stack)
        HRESULT hrPend = ConectarInterno(pinReceptor, &copia);
        if (SUCCEEDED(hrPend)) return S_OK;
    }

    // Probar todas las combinaciones nuestras hasta que acepte.
    HRESULT hrUltimo = VFW_E_NO_ACCEPTABLE_TYPES;
    for (int f = 0; f < kFormatos; ++f)
    {
        if (hayPropuesta && mt->subtype != kGuidNulo)
        {
            const GUID& pedido = mt->subtype;
            if (!(pedido == SubtipoPorIndice(f))) continue;
        }
        for (int t = 0; t < kTamanos; ++t)
        {
            GUID sub = SubtipoPorIndice(f);
            VIDEOINFOHEADER vih = {};
            vih.AvgTimePerFrame = kDuracionFrame;
            vih.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            vih.bmiHeader.biWidth = g_tamanos[t].w;
            vih.bmiHeader.biHeight = g_tamanos[t].h;
            vih.bmiHeader.biPlanes = 1;
            vih.bmiHeader.biBitCount = (f == 0) ? 12 : ((f == 1) ? 24 : 32);
            vih.bmiHeader.biCompression = (f == 0) ? 'I420' : BI_RGB;
            vih.bmiHeader.biSizeImage = BytesDeFormato(sub, g_tamanos[t].w, g_tamanos[t].h);

            // QueryAccept del par: primer paso economico
            AM_MEDIA_TYPE candidato = {};
            candidato.majortype = MEDIATYPE_Video;
            candidato.subtype = sub;
            candidato.formattype = FORMAT_VideoInfo;
            candidato.bFixedSizeSamples = TRUE;
            candidato.bTemporalCompression = FALSE;
            candidato.lSampleSize = BytesDeFormato(sub, g_tamanos[t].w, g_tamanos[t].h);
            candidato.cbFormat = sizeof(vih);
            candidato.pbFormat = reinterpret_cast<BYTE*>(&vih);

            HRESULT qa = pinReceptor->QueryAccept(&candidato);
            if (FAILED(qa) || qa == S_FALSE) { hrUltimo = VFW_E_NO_ACCEPTABLE_TYPES; continue; }

            HRESULT hr = ConectarInterno(pinReceptor, &candidato);
            if (SUCCEEDED(hr)) return S_OK;
            hrUltimo = hr;
        }
    }
    return hrUltimo;
}

STDMETHODIMP LenscordPin::ReceiveConnection(IPin* conector, const AM_MEDIA_TYPE* mt)
{
    if (!conector || !mt) return E_POINTER;
    // Los pines de salida normalmente no reciben ReceiveConnection, pero
    // aceptamos la ruta directa por robustez.
    return ConectarInterno(conector, mt);
}

void LenscordPin::DesconectarInterno()
{
    // igual que en Limpiar: con hilo zombi no se libera nada rastreado por el
    // worker (allocator, pin de entrada, buffers)
    if (!PararHilo()) return;
    LogDiagLinea(L"disconnect");
    Guardia g(&m_filtro->m_cs);
    if (m_allocador)
    {
        m_allocador->Decommit();
        m_allocador->Release();
        m_allocador = nullptr;
    }
    if (m_entrada) { m_entrada->Release(); m_entrada = nullptr; }
    if (m_pinEntrada) { m_pinEntrada->Release(); m_pinEntrada = nullptr; }
    if (m_mtConectado.pbFormat) CoTaskMemFree(m_mtConectado.pbFormat);
    ZeroMemory(&m_mtConectado, sizeof(m_mtConectado));
    ZeroMemory(&m_subtipoConectado, sizeof(m_subtipoConectado));
    m_wConectado = kAncho;
    m_hConectado = kAlto;
}

STDMETHODIMP LenscordPin::Disconnect()
{
    if (!HayConexion()) return VFW_E_NOT_CONNECTED;
    DesconectarInterno();
    return S_OK;
}

STDMETHODIMP LenscordPin::ConnectedTo(IPin** pin)
{
    if (!pin) return E_POINTER;
    *pin = nullptr;
    Guardia g(&m_filtro->m_cs);
    if (!m_pinEntrada) return VFW_E_NOT_CONNECTED;
    *pin = m_pinEntrada;
    (*pin)->AddRef();
    return S_OK;
}

STDMETHODIMP LenscordPin::ConnectionMediaType(AM_MEDIA_TYPE* mt)
{
    if (!mt) return E_POINTER;
    ZeroMemory(mt, sizeof(AM_MEDIA_TYPE));
    Guardia g(&m_filtro->m_cs);
    if (!m_pinEntrada) return VFW_E_NOT_CONNECTED;
    *mt = m_mtConectado;
    mt->pUnk = nullptr;
    mt->pbFormat = nullptr;
    if (m_mtConectado.pbFormat && m_mtConectado.cbFormat > 0)
    {
        mt->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(m_mtConectado.cbFormat));
        if (!mt->pbFormat) return E_OUTOFMEMORY;
        memcpy(mt->pbFormat, m_mtConectado.pbFormat, m_mtConectado.cbFormat);
    }
    return S_OK;
}

STDMETHODIMP LenscordPin::QueryPinInfo(PIN_INFO* info)
{
    if (!info) return E_POINTER;
    wcscpy_s(info->achName, L"Lenscord");
    info->dir = PINDIR_OUTPUT;
    info->pFilter = m_filtro;
    if (info->pFilter) info->pFilter->AddRef();
    return S_OK;
}

STDMETHODIMP LenscordPin::QueryDirection(PIN_DIRECTION* dir)
{
    if (!dir) return E_POINTER;
    *dir = PINDIR_OUTPUT;
    return S_OK;
}

STDMETHODIMP LenscordPin::QueryId(LPWSTR* id)
{
    if (!id) return E_POINTER;
    const size_t bytes = (wcslen(L"Lenscord") + 1) * sizeof(wchar_t);
    *id = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
    if (!*id) return E_OUTOFMEMORY;
    wcscpy_s(*id, wcslen(L"Lenscord") + 1, L"Lenscord");
    return S_OK;
}

STDMETHODIMP LenscordPin::QueryAccept(const AM_MEDIA_TYPE* mt)
{
    if (!mt) return E_POINTER;
    if (!EsFormatoValido(mt)) LogDiagLinea(L"QueryAccept rechazado: subtipo=%08lX", mt->subtype.Data1);
    return EsFormatoValido(mt) ? S_OK : S_FALSE;
}

STDMETHODIMP LenscordPin::EnumMediaTypes(IEnumMediaTypes** e)
{
    if (!e) return E_POINTER;
    EnumMediaTipos* en = new (std::nothrow) EnumMediaTipos(this, 0);
    if (!en) return E_OUTOFMEMORY;
    *e = en;   // refcount inicial = 1
    return S_OK;
}

STDMETHODIMP LenscordPin::QueryInternalConnections(IPin** apin, ULONG* n)
{
    if (!apin || !n) return E_POINTER;
    *n = 0;
    return E_NOTIMPL;
}

// ---------------------------------------------------------------------------
// IAMStreamConfig
// ---------------------------------------------------------------------------

// Construye el mt i-esimo (f = i / kTamanos, t = i % kTamanos) opcionalmente
// con VIDEO_STREAM_CONFIG_CAPS en el buffer "caps".
HRESULT LenscordPin::CopiarFormato(int i, AM_MEDIA_TYPE** mt, BYTE* caps)
{
    if (i < 0 || i >= kTamanos * kFormatos) return S_FALSE;
    if (!mt) return E_POINTER;

    const int f = i / kTamanos;
    const int t = i % kTamanos;
    const GUID& sub = SubtipoPorIndice(f);
    const int w = g_tamanos[t].w;
    const int h = g_tamanos[t].h;

    VIDEOINFOHEADER vih = {};
    vih.AvgTimePerFrame = kDuracionFrame;
    vih.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    vih.bmiHeader.biWidth = w;
    vih.bmiHeader.biHeight = h;
    vih.bmiHeader.biPlanes = 1;
    vih.bmiHeader.biBitCount = (f == 0) ? 12 : ((f == 1) ? 24 : 32);
    vih.bmiHeader.biCompression = (f == 0) ? 'I420' : BI_RGB;
    vih.bmiHeader.biSizeImage = BytesDeFormato(sub, w, h);

    AM_MEDIA_TYPE* nuevo = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (!nuevo) return E_OUTOFMEMORY;
    ZeroMemory(nuevo, sizeof(AM_MEDIA_TYPE));
    nuevo->majortype = MEDIATYPE_Video;
    nuevo->subtype = sub;
    nuevo->bFixedSizeSamples = TRUE;
    nuevo->bTemporalCompression = FALSE;
    nuevo->lSampleSize = vih.bmiHeader.biSizeImage;
    nuevo->formattype = FORMAT_VideoInfo;
    nuevo->pUnk = nullptr;
    nuevo->cbFormat = sizeof(vih);
    nuevo->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(sizeof(vih)));
    if (!nuevo->pbFormat) { CoTaskMemFree(nuevo); return E_OUTOFMEMORY; }
    memcpy(nuevo->pbFormat, &vih, sizeof(vih));

    if (caps)
    {
        auto* v = reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(caps);
        ZeroMemory(v, sizeof(VIDEO_STREAM_CONFIG_CAPS));
        v->guid = FORMAT_VideoInfo;
        v->VideoStandard = 0;
        v->InputSize.cx = kAncho;               v->InputSize.cy = kAlto;
        v->MinCroppingSize.cx = w;              v->MinCroppingSize.cy = h;
        v->MaxCroppingSize.cx = w;              v->MaxCroppingSize.cy = h;
        v->CropGranularityX = 1;                v->CropGranularityY = 1;
        v->CropAlignX = 1;                      v->CropAlignY = 1;
        v->MinOutputSize.cx = w;                v->MinOutputSize.cy = h;
        v->MaxOutputSize.cx = w;                v->MaxOutputSize.cy = h;
        v->OutputGranularityX = 1;              v->OutputGranularityY = 1;
        v->StretchTapsX = 0;                    v->StretchTapsY = 0;
        v->ShrinkTapsX = 0;                     v->ShrinkTapsY = 0;
        v->MinFrameInterval = kDuracionFrame;
        v->MaxFrameInterval = kDuracionFrame;
        v->MinBitsPerSecond = static_cast<long>(vih.bmiHeader.biSizeImage) * 8 * kFps;
        v->MaxBitsPerSecond = v->MinBitsPerSecond;
    }

    *mt = nuevo;
    return S_OK;
}

STDMETHODIMP LenscordPin::SetFormat(AM_MEDIA_TYPE* mt)
{
    if (!mt) return E_POINTER;
    Guardia g(&m_filtro->m_cs);
    if (!EsFormatoValido(mt)) return VFW_E_INVALIDMEDIATYPE;

    int wp = kAncho, hp = kAlto;
    const bool dimsOk = DimensionesDelFormato(mt, &wp, &hp);

    // Ya conectado: aceptar SIEMPRE como los drivers virtuales (libwebrtc
    // conecta y pide formato despues; rechazar aqui es el error 2014 de
    // Discord). Si coincide con lo conectado, nada que hacer.
    if (HayConexion() && mt->subtype == m_mtConectado.subtype &&
        wp == m_wConectado && hp == m_hConectado)
        return S_OK;

    if (m_mtSeleccionado.pbFormat) CoTaskMemFree(m_mtSeleccionado.pbFormat);
    m_mtSeleccionado = *mt;
    m_mtSeleccionado.pUnk = nullptr;
    m_mtSeleccionado.pbFormat = nullptr;
    m_mtSeleccionado.pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(mt->cbFormat));
    if (!m_mtSeleccionado.pbFormat) { m_mtSeleccionado.cbFormat = 0; return E_OUTOFMEMORY; }
    memcpy(m_mtSeleccionado.pbFormat, mt->pbFormat, mt->cbFormat);

    m_pendiente = true;
    m_subtipoPendiente = mt->subtype;
    m_wPendiente = wp;
    m_hPendiente = hp;
    if (!dimsOk)
    {
        m_wPendiente = kAncho;
        m_hPendiente = kAlto;
    }
    LogDiagLinea(L"setformat: %s %dx%d conectado=%s",
        SubTipoNombre(mt->subtype), wp, hp,
        HayConexion() ? L"si (afecta a la siguiente conexion)" : L"no");
    return S_OK;
}

STDMETHODIMP LenscordPin::GetFormat(AM_MEDIA_TYPE** mt)
{
    if (!mt) return E_POINTER;
    *mt = nullptr;
    Guardia g(&m_filtro->m_cs);
    const AM_MEDIA_TYPE* fuente = nullptr;
    // Hallazgo 2: una vez conectado, el formato que manda es el conectado
    // (los frames salen en ese formato); el ultimo SetFormat pendiente solo
    // aplica a la siguiente conexion.
    if (HayConexion() && m_mtConectado.pbFormat)
        fuente = &m_mtConectado;
    else if (m_mtSeleccionado.pbFormat)
        fuente = &m_mtSeleccionado;
    else
    {
        AM_MEDIA_TYPE* defecto = nullptr;
        HRESULT hr = CopiarFormato(0, &defecto, nullptr);
        if (FAILED(hr)) return hr;
        *mt = defecto;
        return S_OK;
    }

    AM_MEDIA_TYPE* copia = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (!copia) return E_OUTOFMEMORY;
    *copia = *fuente;
    copia->pUnk = nullptr;
    copia->pbFormat = nullptr;
    if (fuente->pbFormat && fuente->cbFormat > 0)
    {
        copia->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(fuente->cbFormat));
        if (!copia->pbFormat) { CoTaskMemFree(copia); return E_OUTOFMEMORY; }
        memcpy(copia->pbFormat, fuente->pbFormat, fuente->cbFormat);
    }
    *mt = copia;    return S_OK;
}

STDMETHODIMP LenscordPin::GetNumberOfCapabilities(int* cuenta, int* tamanioCaps)
{
    if (!cuenta || !tamanioCaps) return E_POINTER;
    *cuenta = kTamanos * kFormatos;
    *tamanioCaps = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP LenscordPin::GetStreamCaps(int i, AM_MEDIA_TYPE** mt, BYTE* caps)
{
    if (!mt || !caps) return E_POINTER;
    const HRESULT hr = CopiarFormato(i, mt, caps);
    return (hr == S_OK) ? S_OK : VFW_S_NO_MORE_ITEMS;
}

// ---------------------------------------------------------------------------
// IKsPropertySet (ambas IIDs: la de strmif y la de ksproxy)
// ---------------------------------------------------------------------------
STDMETHODIMP LenscordPin::Set(REFGUID, DWORD, void*, DWORD, void*, DWORD)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP LenscordPin::Get(REFGUID propSet, DWORD propId, void* /*datosInstancia*/, DWORD /*cbInstancia*/,
    void* datosProp, DWORD cbProp, DWORD* cbDevuelto)
{
    if (cbDevuelto) *cbDevuelto = 0;
    if (propSet == s_propSetPin && propId == 0 /*AMPROPERTY_PIN_CATEGORY*/)
    {
        if (cbProp < sizeof(GUID)) return HRESULT_FROM_WIN32(ERROR_MORE_DATA);
        if (datosProp)
            memcpy(datosProp, &s_pinCapture, sizeof(GUID));
        if (cbDevuelto) *cbDevuelto = sizeof(GUID);
        return S_OK;
    }
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP LenscordPin::QuerySupported(REFGUID propSet, DWORD propId, DWORD* soporte)
{
    if (soporte) *soporte = 0;
    if (propSet == s_propSetPin && propId == 0)
    {
        if (soporte) *soporte = KSPROPERTY_SUPPORT_GET;
        return S_OK;
    }
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// ---------------------------------------------------------------------------
// Worker: entrega a 30 fps
// ---------------------------------------------------------------------------

HRESULT LenscordPin::TiempoActualStream(REFERENCE_TIME* t)
{
    // Usamos el reloj del grafo si existe; QPC-propio si no (tiempo de
    // stream monotono anclado al arranque del hilo).
    IReferenceClock* reloj = nullptr;
    {
        Guardia g(&m_filtro->m_cs);
        reloj = m_filtro->m_reloj;
        if (reloj)
        {
            REFERENCE_TIME tt = 0;
            if (SUCCEEDED(reloj->GetTime(&tt)) && tt > 0)
            {
                *t = tt - m_filtro->m_tInicioRun;
                if (*t < 0) *t = 0;
                return S_OK;
            }
        }
    }
    // stream time sintetico
    LARGE_INTEGER qpc, frec;
    QueryPerformanceCounter(&qpc);
    QueryPerformanceFrequency(&frec);
    *t = static_cast<REFERENCE_TIME>(((qpc.QuadPart - m_baseQpc) * 10000000) / frec.QuadPart);
    return S_OK;
}

HRESULT LenscordPin::IniciarHilo()
{
    Guardia g(&m_filtro->m_cs);
    if (m_hilo) return S_OK;
    if (m_hiloColgado && WaitForSingleObject(m_hiloColgado, 0) == WAIT_OBJECT_0)
    {
        // el hilo zombi termino por su cuenta: ya se puede armar otro
        CloseHandle(m_hiloColgado);
        m_hiloColgado = nullptr;
        LogDiagLinea(L"hilo zombi termino solo: marcador limpiado");
    }
    if (m_hiloColgado)
    {
        // Hallazgo 3 (post-arreglo): no se arma un segundo hilo sobre el mismo
        // pin mientras el anterior (zombi, no muerto) siga existiendo.
        LogDiagLinea(L"iniciar hilo denegado: queda un hilo zombi en este pin");
        return E_FAIL;
    }

    // Hallazgo 1: el allocator que ofrece el sink (CLSID_MemoryAllocator de
    // Discord) llega sin comprometer; el hilo de entrega muere en GetBuffer
    // con VFW_E_NOT_COMMITTED y no sale ningun frame. El stream es del filtro:
    // aqui se garantiza el Commit del allocator elegido, sea proprio o del sink.
    // (Pause/Run sin conexion crashearian sobre null: por eso el if)
    if (m_allocador)
    {
        const HRESULT hrC = m_allocador->Commit();
        LogDiagLinea(L"commit del allocator: hr=0x%08X", hrC);
        if (FAILED(hrC)) return hrC;
    }

    if (!m_evSalir)
    {
        m_evSalir = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!m_evSalir) return E_FAIL;
    }
    ResetEvent(m_evSalir);

    LARGE_INTEGER qpc;
    QueryPerformanceCounter(&qpc);
    m_baseQpc = qpc.QuadPart;
    m_tProximoStream = 0;
    m_tengoPrimerFrame = false;
    m_framesEntregados = 0;
    m_fallosGetBuf = 0;
    m_fallosReceive = 0;
    m_runFlag = 1;
    LogDiagLinea(L"hilo de entrega armado");

    HANDLE hilo = CreateThread(nullptr, 0, HiloEntrega, this, 0, nullptr);
    if (!hilo)
    {
        m_runFlag = 0;
        LogDiagLinea(L"CreateThread fallo: err=%lu", GetLastError());
        return E_FAIL;
    }
    m_hilo = hilo;
    return S_OK;
}

bool LenscordPin::PararHilo()   // false: quedo zombi (se conserva el handle, no se mata)
{
    HANDLE hilo = nullptr;
    {
        Guardia g(&m_filtro->m_cs);
        hilo = m_hilo;
        m_hilo = nullptr;
        m_runFlag = 0;
    }
    if (!hilo) return true;
    if (m_evSalir) SetEvent(m_evSalir);
    const DWORD espera = WaitForSingleObject(hilo, 3000);
    if (espera == WAIT_OBJECT_0)
    {
        CloseHandle(hilo);
        if (m_evSalir) ResetEvent(m_evSalir);
        return true;
    }
    // Sin TerminateThread (riesgo de corrupcion de heap en el huesped).
    // El hilo queda zombi: el handle se conserva para que IniciarHilo no arme
    // otro encima, y el objeto se conserva vivo (ver ~LenscordFilter).
    {
        Guardia g(&m_filtro->m_cs);
        m_hiloColgado = hilo;
    }
    LogDiagLinea(L"hilo de entrega no salio en 3000 ms: zombi conservado, no se mata");
    if (m_evSalir) ResetEvent(m_evSalir);
    return false;
}

void LenscordPin::Limpiar()
{
    // hilo zombi: el estado queda vivo para que el worker no toque memoria
    // liberada; el objeto entero se filtra (~LenscordFilter hace return antes)
    if (!PararHilo()) return;
    if (m_allocador)
    {
        m_allocador->Decommit();
        m_allocador->Release();
        m_allocador = nullptr;
    }
    if (m_entrada) { m_entrada->Release(); m_entrada = nullptr; }
    if (m_pinEntrada) { m_pinEntrada->Release(); m_pinEntrada = nullptr; }
    if (m_mtConectado.pbFormat) CoTaskMemFree(m_mtConectado.pbFormat);
    ZeroMemory(&m_mtConectado, sizeof(m_mtConectado));
    if (m_mtSeleccionado.pbFormat) CoTaskMemFree(m_mtSeleccionado.pbFormat);
    ZeroMemory(&m_mtSeleccionado, sizeof(m_mtSeleccionado));
    if (m_evSalir) { CloseHandle(m_evSalir); m_evSalir = nullptr; }
}

DWORD WINAPI LenscordPin::HiloEntrega(LPVOID param)
{
    auto* pin = static_cast<LenscordPin*>(param);
    pin->BucleEntrega();
    return 0;
}

void LenscordPin::BucleEntrega()
{
    LARGE_INTEGER frec;
    QueryPerformanceFrequency(&frec);
    const LONGLONG pasoQpc = frec.QuadPart / kFps;   // 33.3 ms en tics QPC

    // Diagnostico temporal (ver LenscordDiag abajo)
    InterlockedExchange(&g_diag[0], 1);   // hilo armado
    LogDiagLinea(L"hilo de entrega: inicio");

    if (m_frameNV12.size() < static_cast<size_t>(kTamanoFrame))
        m_frameNV12.resize(kTamanoFrame);

    LARGE_INTEGER proximo;
    QueryPerformanceCounter(&proximo);

    for (;;)
    {
        // corte de salida en piezas de 10 ms: el ritmo real lo fija
        // "proximo" (33.3 ms por frame); esta espera solo revisa salida.
        LARGE_INTEGER tW0;
        QueryPerformanceCounter(&tW0);
        InterlockedCompareExchange(&g_diag[16], tW0.LowPart, 0);   // QPC de la primera iteracion
        g_diag[17] = tW0.LowPart;                                  // QPC de la ultima iteracion
        const DWORD espera = WaitForSingleObject(m_evSalir, 10);
        LARGE_INTEGER tW1;
        QueryPerformanceCounter(&tW1);
        InterlockedExchangeAdd(&g_diag[14], (LONG)((tW1.QuadPart - tW0.QuadPart) * 1000 / frec.QuadPart));  // ms espera externa
        if (espera == WAIT_OBJECT_0)
        {
            LogDiagLinea(L"hilo sale: evSalir, frames=%ld", m_framesEntregados);
            break;
        }

        InterlockedIncrement(&g_diag[1]);   // iteraciones
        if (!m_runFlag)
        {
            LogDiagLinea(L"hilo sale: runFlag=0 (Stop/Pause), frames=%ld", m_framesEntregados);
            break;
        }
        if (!m_allocador || !m_entrada)
            continue;   // no conectado: mantener el hilo vivo hasta Stop
        InterlockedIncrement(&g_diag[2]);   // conectados (paso del if)

        // ritmo
        LARGE_INTEGER tP0;
        QueryPerformanceCounter(&tP0);
        LARGE_INTEGER ahora;
        QueryPerformanceCounter(&ahora);
        if (proximo.QuadPart > ahora.QuadPart)
        {
            const LONGLONG resto = proximo.QuadPart - ahora.QuadPart;
            const DWORD ms = static_cast<DWORD>((resto * 1000) / frec.QuadPart);
            if (ms > 0 && WaitForSingleObject(m_evSalir, ms) == WAIT_OBJECT_0)
            {
                LogDiagLinea(L"hilo sale: evSalir (durante ritmo), frames=%ld", m_framesEntregados);
                break;
            }
        }
        else if (proximo.QuadPart < ahora.QuadPart - pasoQpc * 60)
        {
            proximo.QuadPart = ahora.QuadPart;   // re-anclar tras un bache
        }
        proximo.QuadPart += pasoQpc;
        LARGE_INTEGER tP1;
        QueryPerformanceCounter(&tP1);
        InterlockedExchangeAdd(&g_diag[15], (LONG)((tP1.QuadPart - tP0.QuadPart) * 1000 / frec.QuadPart));  // ms espera ritmo

        // cronometraje de etapa (diagnostico)
        LARGE_INTEGER t0, t1, tFrec;
        QueryPerformanceCounter(&t0);
        QueryPerformanceFrequency(&tFrec);

        // frame fuente
        if (!m_pool.CopiarUltimo(m_frameNV12.data()))
        {
            if (!m_tengoFrame)
            {
                LlenarStandbyNV12(m_frameNV12.data());
            }
            // si ya hubo frame bueno: conservarlo unos ciclos (sin parpadeo)
        }
        else
        {
            m_tengoFrame = true;
        }
        QueryPerformanceCounter(&t1);
        InterlockedExchangeAdd(&g_diag[11], (LONG)((t1.QuadPart - t0.QuadPart) * 1000 / tFrec.QuadPart));   // us CopiarUltimo

        // datos del formato a entregar: Hallazgo 2. Una vez conectado, las
        // muestras salen SIEMPRE en el formato conectado; un SetFormat
        // posterior queda pendiente para la siguiente conexion.
        GUID sub;
        int w, h;
        {
            Guardia g(&m_filtro->m_cs);
            sub = m_subtipoConectado;
            w = m_wConectado;
            h = m_hConectado;
        }
        const int len = BytesDeFormato(sub, w, h);
        if (len <= 0)
            continue;

        IMediaSample* sample = nullptr;
        HRESULT hr = m_allocador->GetBuffer(&sample, nullptr, nullptr, 0);
        LARGE_INTEGER t2;
        QueryPerformanceCounter(&t2);
        InterlockedExchangeAdd(&g_diag[12], (LONG)((t2.QuadPart - t1.QuadPart) * 1000 / tFrec.QuadPart));   // us GetBuffer
        if (hr != S_OK || !sample)
        {
            InterlockedExchange(&g_diag[3], (LONG)hr);   // ultimo hr de GetBuffer
            InterlockedIncrement(&g_diag[4]);
            const LONG veces = InterlockedIncrement(&m_fallosGetBuf);
            if (veces == 1 || veces % 100 == 0)
                LogDiagLinea(L"getbuf fallo: hr=0x%08X (%s), vez=%ld",
                    hr, hr == VFW_E_NOT_COMMITTED ? L"VFW_E_NOT_COMMITTED" : L"otro", veces);
            continue;   // hallazgo 1: no terminar en silencio; el hr queda registrado
        }
        InterlockedIncrement(&g_diag[5]);               // GetBuffer OK

        BYTE* destino = nullptr;
        if (SUCCEEDED(sample->GetPointer(&destino)) && destino)
        {
            int lenEscritura = len;
            const long capacidad = sample->GetSize();
            if (capacidad > 0 && lenEscritura > capacidad)
                lenEscritura = static_cast<int>(capacidad);   // defensa del allocator ajeno
            ConvertirNV12(m_frameNV12.data(), destino, sub, w, h);
            sample->SetActualDataLength(lenEscritura);

            REFERENCE_TIME t = 0;
            TiempoActualStream(&t);
            REFERENCE_TIME tFin = t + kDuracionFrame;
            sample->SetTime(&t, &tFin);
            sample->SetSyncPoint(TRUE);
            sample->SetDiscontinuity(m_tengoPrimerFrame ? FALSE : TRUE);
            m_tengoPrimerFrame = true;

            LARGE_INTEGER t3;
            QueryPerformanceCounter(&t3);
            InterlockedExchangeAdd(&g_diag[9], (LONG)((t3.QuadPart - t2.QuadPart) * 1000 / tFrec.QuadPart));   // us conversion

            const HRESULT hrR = m_entrada->Receive(sample);
            LARGE_INTEGER t4;
            QueryPerformanceCounter(&t4);
            InterlockedExchangeAdd(&g_diag[10], (LONG)((t4.QuadPart - t3.QuadPart) * 1000 / tFrec.QuadPart));  // us Receive
            g_diag[18] = t4.LowPart;                   // QPC tras el ultimo Receive

            InterlockedExchange(&g_diag[6], (LONG)hrR);
            InterlockedIncrement(&g_diag[7]);           // Receives
            InterlockedExchangeAdd(&g_diag[8], (LONG)((t4.QuadPart - t0.QuadPart) * 1000 / tFrec.QuadPart));   // us ciclo total
            InterlockedIncrement(&g_diag[13]);

            if (SUCCEEDED(hrR))
            {
                const LONG frames = InterlockedIncrement(&m_framesEntregados);
                if (frames == 1)
                {
                    LARGE_INTEGER tF;
                    QueryPerformanceCounter(&tF);
                    const LONGLONG primerMs = (tF.QuadPart - m_baseQpc) * 1000 / frec.QuadPart;
                    LogDiagLinea(L"primer frame: +%lld ms, len=%d, hr=0x%08X", primerMs, len, hrR);
                }
                if (frames <= 5 || frames % 300 == 0)
                    LogDiagLinea(L"frame %ld len=%d hr=0x%08X", frames, len, hrR);
            }
            else
            {
                const LONG vecesR = InterlockedIncrement(&m_fallosReceive);
                if (vecesR == 1 || vecesR % 100 == 0)
                    LogDiagLinea(L"receive fallo: hr=0x%08X, len=%d, vez=%ld", hrR, len, vecesR);
            }
        }
        sample->Release();
    }
}

// Diagnostico temporal: array de 8 LONG desde el proceso huesped.
// [0] hilo armado | [1] iteraciones | [2] conectados | [3] hr GetBuffer |
// [4] fallos GetBuffer | [5] GetBuffer OK | [6] hr Receive | [7] Receives
extern "C" __declspec(dllexport) const LONG* __stdcall LenscordDiag()
{
    return const_cast<const LONG*>(static_cast<const volatile LONG*>(g_diag));
}

// ---------------------------------------------------------------------------
// Clase de objetos (class factory) + exports
// ---------------------------------------------------------------------------
class FactoriaLenscord : public IClassFactory
{
public:
    FactoriaLenscord() { InterlockedIncrement(&g_objetosVivos); }
    ~FactoriaLenscord() { InterlockedDecrement(&g_objetosVivos); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown)
            *ppv = static_cast<IUnknown*>(this);
        else if (riid == IID_IClassFactory)
            *ppv = static_cast<IClassFactory*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const ULONG r = InterlockedDecrement(&m_refs);
        if (r == 0) delete this;
        return r;
    }

    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        LenscordFilter* filtro = new (std::nothrow) LenscordFilter();
        if (!filtro) return E_OUTOFMEMORY;
        LogDiagLinea(L"filtro instanciado (pid=%lu)", GetCurrentProcessId());
        if (!filtro->ElPin())
        {
            delete filtro;
            return E_OUTOFMEMORY;
        }
        // El filtro arranca con refcount 0; QueryInterface entrega al cliente
        // la unica referencia. Si falla, el objeto se destruye directo.
        HRESULT hr = filtro->QueryInterface(riid, ppv);
        if (FAILED(hr)) delete filtro;
        return hr;
    }

    STDMETHODIMP LockServer(BOOL) override { return S_OK; }

private:
    LONG m_refs = 1;
};

// Exports: SIN __declspec(dllexport) (el SDK ya declara estas APIs y MSVC
// rechaza redeclararlas con vinculacion distinta); los nombres salen por
// LenscordDShow.def.
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (rclsid != CLSID_LenscordDShow) return CLASS_E_CLASSNOTAVAILABLE;
    FactoriaLenscord* factoria = new (std::nothrow) FactoriaLenscord();
    if (!factoria) return E_OUTOFMEMORY;
    HRESULT hr = factoria->QueryInterface(riid, ppv);
    factoria->Release();
    return hr;
}

STDAPI DllCanUnloadNow(void)
{
    return (InterlockedCompareExchange(&g_objetosVivos, 0, 0) == 0) ? S_OK : S_FALSE;
}

// ---------------------------------------------------------------------------
// Registro / anular registro
// ---------------------------------------------------------------------------

static bool EscribirHerenciaClase()
{
    wchar_t ruta[MAX_PATH * 2] = {};
    GetModuleFileNameW(g_hModulo, ruta, MAX_PATH);

    wchar_t rutaClave[256];
    swprintf_s(rutaClave, L"SOFTWARE\\Classes\\CLSID\\{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        CLSID_LenscordDShow.Data1, CLSID_LenscordDShow.Data2, CLSID_LenscordDShow.Data3,
        CLSID_LenscordDShow.Data4[0], CLSID_LenscordDShow.Data4[1],
        CLSID_LenscordDShow.Data4[2], CLSID_LenscordDShow.Data4[3],
        CLSID_LenscordDShow.Data4[4], CLSID_LenscordDShow.Data4[5],
        CLSID_LenscordDShow.Data4[6], CLSID_LenscordDShow.Data4[7]);

    HKEY h = nullptr;
    DWORD disp = 0;
    if (!SUCCEEDED(RegCreateKeyExW(HKEY_LOCAL_MACHINE, rutaClave, 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &h, &disp)))
        return false;
    RegSetValueExW(h, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(kNombreFiltro),
        static_cast<DWORD>((wcslen(kNombreFiltro) + 1) * sizeof(wchar_t)));

    HKEY hInproc = nullptr;
    bool ok = SUCCEEDED(RegCreateKeyExW(h, L"InprocServer32", 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_SET_VALUE, nullptr, &hInproc, &disp));
    if (ok)
    {
        RegSetValueExW(hInproc, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(ruta),
            static_cast<DWORD>((wcslen(ruta) + 1) * sizeof(wchar_t)));
        const wchar_t* tm = L"Both";
        RegSetValueExW(hInproc, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(tm),
            static_cast<DWORD>((wcslen(tm) + 1) * sizeof(wchar_t)));
        RegCloseKey(hInproc);
    }
    RegCloseKey(h);
    return ok;
}

STDAPI DllRegisterServer()
{
    const HRESULT hrCo = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    HRESULT hr = E_FAIL;

    if (!EscribirHerenciaClase())
    {
        if (SUCCEEDED(hrCo)) CoUninitialize();
        return SELFREG_E_CLASS;
    }

    IFilterMapper2* fm = nullptr;
    hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
        IID_IFilterMapper2, reinterpret_cast<void**>(&fm));
    if (SUCCEEDED(hr) && fm)
    {
        REGPINTYPES tipos[3] = {};
        tipos[0].clsMajorType = &MEDIATYPE_Video;
        tipos[0].clsMinorType = gpSubI420;
        tipos[1].clsMajorType = &MEDIATYPE_Video;
        tipos[1].clsMinorType = &MEDIASUBTYPE_RGB24;
        tipos[2].clsMajorType = &MEDIATYPE_Video;
        tipos[2].clsMinorType = &MEDIASUBTYPE_RGB32;

        REGFILTERPINS2 pin = {};
        pin.dwFlags = REG_PINFLAG_B_OUTPUT;
        pin.cInstances = 1;
        pin.nMediaTypes = 3;
        pin.lpMediaType = tipos;
        pin.nMediums = 0;
        pin.lpMedium = nullptr;
        pin.clsPinCategory = &s_pinCapture;

        REGFILTER2 rf2 = {};
        rf2.dwVersion = 2;
        rf2.dwMerit = MERIT_DO_NOT_USE;
        rf2.cPins2 = 1;
        rf2.rgPins2 = &pin;

        hr = fm->RegisterFilter(CLSID_LenscordDShow, kNombreFiltro, nullptr,
            &CLSID_VideoInputDeviceCategory, kNombreInstancia, &rf2);
        fm->Release();
    }

    if (SUCCEEDED(hrCo)) CoUninitialize();
    return SUCCEEDED(hr) ? S_OK : SELFREG_E_CLASS;
}

STDAPI DllUnregisterServer()
{
    const HRESULT hrCo = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    HRESULT hr = S_OK;

    IFilterMapper2* fm = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER,
        IID_IFilterMapper2, reinterpret_cast<void**>(&fm))) && fm)
    {
        hr = fm->UnregisterFilter(&CLSID_VideoInputDeviceCategory, kNombreInstancia, CLSID_LenscordDShow);
        fm->Release();
    }

    wchar_t rutaClase[256];
    swprintf_s(rutaClase, L"SOFTWARE\\Classes\\CLSID\\{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        CLSID_LenscordDShow.Data1, CLSID_LenscordDShow.Data2, CLSID_LenscordDShow.Data3,
        CLSID_LenscordDShow.Data4[0], CLSID_LenscordDShow.Data4[1],
        CLSID_LenscordDShow.Data4[2], CLSID_LenscordDShow.Data4[3],
        CLSID_LenscordDShow.Data4[4], CLSID_LenscordDShow.Data4[5],
        CLSID_LenscordDShow.Data4[6], CLSID_LenscordDShow.Data4[7]);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, rutaClase);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE,
        L"SOFTWARE\\Classes\\CLSID\\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\\Instance\\Lenscord");

    if (SUCCEEDED(hrCo)) CoUninitialize();
    return S_OK;
}

// ---------------------------------------------------------------------------
// DllMain
// ---------------------------------------------------------------------------
extern "C" BOOL WINAPI DllMain(HINSTANCE hinst, DWORD razon, LPVOID)
{
    if (razon == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hinst);
        g_hModulo = hinst;
        timeBeginPeriod(1);
    }
    else if (razon == DLL_PROCESS_DETACH)
    {
        timeEndPeriod(1);
    }
    return TRUE;
}
