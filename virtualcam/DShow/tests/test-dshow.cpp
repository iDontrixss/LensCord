// ---------------------------------------------------------------------------
// test-dshow.exe — pruebas del filtro Lenscord (DirectShow)
//
//   test-dshow.exe factory <ruta\dll>   — class factory SIN registro
//   test-dshow.exe body                 — registro (BD), enumeración y grafo
//
// El modo "body" requiere la DLL ya registrada (regsvr32).
// ---------------------------------------------------------------------------
#define WIN32_LEAN_AND_MEAN
#include <initguid.h>
#include <dshow.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "uuid.lib")

// ---------------------------------------------------------------------------
// GUIDs conocidos a mano (no dependen de uuids.h)
// ---------------------------------------------------------------------------

// {5B1F6468-8F22-4A63-B38C-27357AE6D492} — filtro Lenscord
static const GUID CLSID_LenscordFiltro =
{ 0x5B1F6468, 0x8F22, 0x4A63, { 0xB3, 0x8C, 0x27, 0x35, 0x7A, 0xE6, 0xD4, 0x92 } };

// {C1F400A4-3F08-11D3-9F0B-006008039E37} — NullRenderer (no esta en uuids.h)
static const GUID CLSID_NullRend =
{ 0xC1F400A4, 0x3F08, 0x11D3, { 0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37 } };

// {C1F400A0-3F08-11D3-9F0B-006008039E37} — SampleGrabber (qedit.dll)
static const GUID CLSID_SampleGrabberTest =
{ 0xC1F400A0, 0x3F08, 0x11D3, { 0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37 } };

// {6B652FFF-11FE-4FCE-92AD-0266B5D7C78F} — IID_ISampleGrabber
static const GUID IID_ISampleGrabberTest =
{ 0x6B652FFF, 0x11FE, 0x4FCE, { 0x92, 0xAD, 0x02, 0x66, 0xB5, 0xD7, 0xC7, 0x8F } };

// Copia local de ISampleGrabber (el SDK moderno no trae qedit.h)
struct ISampleGrabberT : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL OneShot) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE* pType) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType(AM_MEDIA_TYPE* pType) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL BufferThem) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample** ppSample) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long* pBufferSize, long* pBuffer) = 0;
};

static const wchar_t kClaveInproc[] =
    L"SOFTWARE\\Classes\\CLSID\\{5B1F6468-8F22-4A63-B38C-27357AE6D492}\\InprocServer32";
static const wchar_t kClaveInstancia[] =
    L"SOFTWARE\\Classes\\CLSID\\{860BB310-5D01-11d0-BD3B-00A0C911CE86}\\Instance\\Lenscord";

// ---------------------------------------------------------------------------
static int Total = 0, Ok = 0, Fallos = 0;

static void Marcar(const wchar_t* nombre, bool paso, const wchar_t* extra = nullptr)
{
    ++Total;
    if (paso) { ++Ok; wprintf(L"  [OK]   %s%s\n", nombre, extra ? extra : L""); }
    else { ++Fallos; wprintf(L"  [MAL]  %s%s\n", nombre, extra ? extra : L""); }
}

static std::wstring TextoGuid(const GUID& g)
{
    wchar_t b[64];
    swprintf_s(b, L"{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
        g.Data1, g.Data2, g.Data3,
        g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3],
        g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
    return b;
}

static bool LecturaValor(const wchar_t* clave, wchar_t* valor, DWORD cbValor)
{
    return RegGetValueW(HKEY_LOCAL_MACHINE, clave, nullptr, RRF_RT_REG_SZ,
        nullptr, valor, &cbValor) == ERROR_SUCCESS;
}

static bool ExisteClave(const wchar_t* clave)
{
    HKEY h = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, clave, 0, KEY_READ, &h) != ERROR_SUCCESS)
        return false;
    RegCloseKey(h);
    return true;
}

// ---------------------------------------------------------------------------
// Modo factory: carga la DLL y ejercita DllGetClassObject sin registro.
// ---------------------------------------------------------------------------
static int ModoFactory(const wchar_t* rutaDll)
{
    wprintf(L"=== factory (sin registro): %s\n", rutaDll);
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    HMODULE m = LoadLibraryW(rutaDll);
    Marcar(L"LoadLibrary", m != nullptr);
    if (!m) { CoUninitialize(); return Fallos ? 1 : 0; }

    const char* nombres[4] = { "DllGetClassObject", "DllCanUnloadNow", "DllRegisterServer", "DllUnregisterServer" };
    for (int i = 0; i < 4; ++i)
    {
        void* f = reinterpret_cast<void*>(GetProcAddress(m, nombres[i]));
        wchar_t extra[80];
        swprintf_s(extra, L"(GetProcAddress %hs)", nombres[i]);
        Marcar(extra, f != nullptr);
    }

    typedef HRESULT (STDAPICALLTYPE* FnGetClassObject)(REFCLSID, REFIID, void**);
    auto* getClass = reinterpret_cast<FnGetClassObject>(GetProcAddress(m, "DllGetClassObject"));
    typedef HRESULT (STDAPICALLTYPE* FnPuedeDescargar)(void);
    auto* puedeDescargar = reinterpret_cast<FnPuedeDescargar>(GetProcAddress(m, "DllCanUnloadNow"));
    if (getClass)
    {
        IClassFactory* factoria = nullptr;
        HRESULT hr = getClass(CLSID_LenscordFiltro, IID_IClassFactory,
            reinterpret_cast<void**>(&factoria));
        Marcar(L"DllGetClassObject -> IClassFactory", hr == S_OK && factoria);
        if (factoria)
        {
            IBaseFilter* filtro = nullptr;
            hr = factoria->CreateInstance(nullptr, IID_IBaseFilter,
                reinterpret_cast<void**>(&filtro));
            Marcar(L"CreateInstance -> IBaseFilter", hr == S_OK && filtro);
            if (filtro)
            {
                FILTER_INFO info = {};
                hr = filtro->QueryFilterInfo(&info);
                wprintf(L"       nombre del filtro: %s (hr=0x%08X)\n", info.achName, hr);
                Marcar(L"QueryFilterInfo", hr == S_OK && wcscmp(info.achName, L"Lenscord") == 0);
                if (info.pGraph) info.pGraph->Release();

                IEnumPins* ep = nullptr;
                hr = filtro->EnumPins(&ep);
                Marcar(L"EnumPins", hr == S_OK && ep);
                if (ep)
                {
                    IPin* pin = nullptr;
                    hr = ep->Next(1, &pin, nullptr);
                    Marcar(L"Obtener pin unico", hr == S_OK && pin);
                    if (pin)
                    {
                        LPWSTR id = nullptr;
                        pin->QueryId(&id);
                        wprintf(L"       id del pin: %s\n", id ? id : L"(null)");
                        if (id) CoTaskMemFree(id);

                        IAMStreamConfig* sc = nullptr;
                        hr = pin->QueryInterface(IID_IAMStreamConfig,
                            reinterpret_cast<void**>(&sc));
                        Marcar(L"pin soporta IAMStreamConfig", hr == S_OK && sc);
                        if (sc)
                        {
                            int cuenta = 0, tamanio = 0;
                            hr = sc->GetNumberOfCapabilities(&cuenta, &tamanio);
                            wprintf(L"       formats: %d (caps %d bytes, hr=0x%08X)\n", cuenta, tamanio, hr);
                            wchar_t msg[96];
                            swprintf_s(msg, L"(GetNumberOfCapabilities, %d formatos)", cuenta);
                            Marcar(msg, hr == S_OK && cuenta == 12 && tamanio == (int)sizeof(VIDEO_STREAM_CONFIG_CAPS));

                            // Probar el formato 0 (I420 1280x720)
                            AM_MEDIA_TYPE* mt = nullptr;
                            std::vector<BYTE> caps(tamanio);
                            hr = sc->GetStreamCaps(0, &mt, caps.data());
                            Marcar(L"GetStreamCaps(0)", hr == S_OK && mt);
                            if (mt)
                            {
                                auto* vih = reinterpret_cast<const VIDEOINFOHEADER*>(mt->pbFormat);
                                wprintf(L"       formato0: %s %ldx%ld bitcount=%d compresion=0x%08X\n",
                                    TextoGuid(mt->subtype).c_str(), vih->bmiHeader.biWidth,
                                    vih->bmiHeader.biHeight, vih->bmiHeader.biBitCount,
                                    (unsigned)vih->bmiHeader.biCompression);
                                if (mt->pbFormat) CoTaskMemFree(mt->pbFormat);
                                CoTaskMemFree(mt);
                            }
                            sc->Release();
                        }
                        pin->Release();
                    }
                    ep->Release();
                }
                filtro->Release();
            }
            factoria->Release();
        }
    }
    if (puedeDescargar)
    {
        const HRESULT hr = puedeDescargar();
        Marcar(L"DllCanUnloadNow == S_OK (objetos liberados)", hr == S_OK);
    }
    FreeLibrary(m);
    CoUninitialize();
    wprintf(L"=== factory: %d/%d pruebas correctas\n", Ok, Total);
    return Fallos ? 1 : 0;
}

// ---------------------------------------------------------------------------
// TsFiltro — sink de pruebas propio, imita el pin de entrada de libwebrtc:
// GetAllocator devuelve E_NOTIMPL para forzar el MemAlloc del filtro. Cuenta
// los samples recibidos y guarda los dos ultimos frames.
// ---------------------------------------------------------------------------
static volatile LONG ts_recibidos = 0;
static volatile LONG ts_ultLen = 0;
static volatile LONG ts_allocExterno = 0;   // 1 si NotifyAllocator recibio un allocator externo
static int g_wNeg = 0, g_hNeg = 0;
static bool g_i420Neg = true;
static std::vector<unsigned char> ts_ultFrame, ts_previoFrame;

// GUID arbitrario para GetClassID del sink de pruebas
static const GUID GUID_DE_PRUEBA_TS =
{ 0xDD5E1111, 0x1111, 0x4CE1, { 0xA0, 0xA0, 0xAA, 0xAA, 0xBB, 0xBB, 0xCC, 0xCC } };

class TsFiltro;
class TsEnumPinsL;

class TsFiltro : public IBaseFilter, public IPin, public IMemInputPin
{
public:
    TsFiltro() = default;
    ~TsFiltro()
    {
        if (m_grafo) { m_grafo->Release(); m_grafo = nullptr; }
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown)
            *ppv = static_cast<IUnknown*>(static_cast<IBaseFilter*>(this));
        else if (riid == IID_IBaseFilter)
            *ppv = static_cast<IBaseFilter*>(this);
        else if (riid == IID_IPin)
            *ppv = static_cast<IPin*>(this);
        else if (riid == IID_IMemInputPin)
            *ppv = static_cast<IMemInputPin*>(this);
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

    // --- IPin (entrada) ------------------------------------------------------
    STDMETHODIMP Connect(IPin*, const AM_MEDIA_TYPE*) override { return E_UNEXPECTED; }
    STDMETHODIMP ReceiveConnection(IPin* pin, const AM_MEDIA_TYPE* mt) override
    {
        if (!pin || !mt) return E_POINTER;
        if (mt->majortype != MEDIATYPE_Video) return VFW_E_TYPE_NOT_ACCEPTED;
        IPin* anterior = m_pinMandante;
        m_pinMandante = pin;
        m_pinMandante->AddRef();
        if (anterior) anterior->Release();
        if (m_mtRecibido.pbFormat) { CoTaskMemFree(m_mtRecibido.pbFormat); m_mtRecibido.pbFormat = nullptr; }
        m_mtRecibido = *mt;
        m_mtRecibido.pUnk = nullptr;
        m_mtRecibido.pbFormat = nullptr;
        if (mt->cbFormat > 0)
        {
            m_mtRecibido.pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(mt->cbFormat));
            if (!m_mtRecibido.pbFormat) return E_OUTOFMEMORY;
            memcpy(m_mtRecibido.pbFormat, mt->pbFormat, mt->cbFormat);
        }
        return S_OK;
    }
    STDMETHODIMP Disconnect() override
    {
        if (!m_pinMandante) return VFW_E_NOT_CONNECTED;
        m_pinMandante->Release();
        m_pinMandante = nullptr;
        if (m_mtRecibido.pbFormat) { CoTaskMemFree(m_mtRecibido.pbFormat); m_mtRecibido.pbFormat = nullptr; }
        return S_OK;
    }
    STDMETHODIMP ConnectedTo(IPin** p) override
    {
        if (!p) return E_POINTER;
        *p = nullptr;
        if (!m_pinMandante) return VFW_E_NOT_CONNECTED;
        *p = m_pinMandante;
        (*p)->AddRef();
        return S_OK;
    }
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* p) override
    {
        if (!p) return E_POINTER;
        ZeroMemory(p, sizeof(*p));
        if (!m_pinMandante) return VFW_E_NOT_CONNECTED;
        *p = m_mtRecibido;
        p->pUnk = nullptr;
        p->pbFormat = nullptr;
        if (m_mtRecibido.pbFormat && m_mtRecibido.cbFormat > 0)
        {
            p->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(m_mtRecibido.cbFormat));
            if (!p->pbFormat) return E_OUTOFMEMORY;
            memcpy(p->pbFormat, m_mtRecibido.pbFormat, m_mtRecibido.cbFormat);
        }
        return S_OK;
    }
    STDMETHODIMP QueryPinInfo(PIN_INFO* info) override
    {
        if (!info) return E_POINTER;
        wcscpy_s(info->achName, L"Entrada");
        info->dir = PINDIR_INPUT;
        info->pFilter = static_cast<IBaseFilter*>(this);
        info->pFilter->AddRef();
        return S_OK;
    }
    STDMETHODIMP QueryDirection(PIN_DIRECTION* dir) override
    {
        if (!dir) return E_POINTER;
        *dir = PINDIR_INPUT;
        return S_OK;
    }
    STDMETHODIMP QueryId(LPWSTR* id) override
    {
        if (!id) return E_POINTER;
        const size_t bytes = (wcslen(L"Entrada") + 1) * sizeof(wchar_t);
        *id = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
        if (!*id) return E_OUTOFMEMORY;
        wcscpy_s(*id, wcslen(L"Entrada") + 1, L"Entrada");
        return S_OK;
    }
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* mt) override
    {
        return (mt && mt->majortype == MEDIATYPE_Video) ? S_OK : S_FALSE;
    }
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes**) override { return E_NOTIMPL; }
    STDMETHODIMP QueryInternalConnections(IPin**, ULONG* n) override
    {
        if (n) *n = 0;
        return E_NOTIMPL;
    }
    STDMETHODIMP EndOfStream() override { return S_OK; }
    STDMETHODIMP BeginFlush() override { return S_OK; }
    STDMETHODIMP EndFlush() override { return S_OK; }
    STDMETHODIMP NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) override { return S_OK; }

    // --- IMemInputPin --------------------------------------------------------
    STDMETHODIMP GetAllocator(IMemAllocator**) override { return E_NOTIMPL; }   // fuerza el MemAlloc del filtro
    STDMETHODIMP NotifyAllocator(IMemAllocator* a, BOOL) override
    {
        if (a) InterlockedExchange(&ts_allocExterno, 1);
        return S_OK;
    }
    STDMETHODIMP GetAllocatorRequirements(ALLOCATOR_PROPERTIES*) override { return E_NOTIMPL; }
    STDMETHODIMP Receive(IMediaSample* s) override
    {
        if (!s) return E_POINTER;
        long len = s->GetActualDataLength();
        BYTE* p = nullptr;
        if (FAILED(s->GetPointer(&p))) return E_FAIL;
        if (p && len > 0)
        {
            InterlockedExchange(&ts_ultLen, len);
            if (!ts_ultFrame.empty()) ts_previoFrame = ts_ultFrame;
            ts_ultFrame.assign(p, p + len);   // Receive no concurre con el test aqui
            InterlockedIncrement(&ts_recibidos);
        }
        return S_OK;
    }
    STDMETHODIMP ReceiveMultiple(IMediaSample** v, long n, long* hechos) override
    {
        if (!v) return E_POINTER;
        long hecho = 0;
        for (long i = 0; i < n; ++i)
        {
            const HRESULT r = Receive(v[i]);
            if (r == S_OK) ++hecho;
        }
        if (hechos) *hechos = hecho;
        return S_OK;
    }
    STDMETHODIMP ReceiveCanBlock() override { return S_OK; }

    // --- IMediaFilter / IBaseFilter ----------------------------------------
    STDMETHODIMP GetClassID(CLSID* clsid) override
    {
        if (!clsid) return E_POINTER;
        *clsid = GUID_DE_PRUEBA_TS;
        return S_OK;
    }
    STDMETHODIMP Stop() override { m_estado = 0; return S_OK; }
    STDMETHODIMP Pause() override { m_estado = 1; return S_OK; }
    STDMETHODIMP Run(REFERENCE_TIME) override { m_estado = 2; return S_OK; }
    STDMETHODIMP GetState(DWORD, FILTER_STATE* st) override
    {
        if (!st) return E_POINTER;
        *st = static_cast<FILTER_STATE>(m_estado);
        return S_OK;
    }
    STDMETHODIMP SetSyncSource(IReferenceClock*) override { return S_OK; }
    STDMETHODIMP GetSyncSource(IReferenceClock** reloj) override
    {
        if (!reloj) return E_POINTER;
        *reloj = nullptr;
        return S_OK;
    }
    STDMETHODIMP EnumPins(IEnumPins** e) override;
    STDMETHODIMP FindPin(LPCWSTR id, IPin** pin) override
    {
        if (!id || !pin) return E_POINTER;
        *pin = nullptr;
        if (wcscmp(id, L"Entrada") != 0) return VFW_E_NOT_FOUND;
        *pin = static_cast<IPin*>(this);
        (*pin)->AddRef();
        return S_OK;
    }
    STDMETHODIMP QueryFilterInfo(FILTER_INFO* info) override
    {
        if (!info) return E_POINTER;
        wcscpy_s(info->achName, L"TestSink");
        info->pGraph = m_grafo;
        if (info->pGraph) info->pGraph->AddRef();
        return S_OK;
    }
    STDMETHODIMP JoinFilterGraph(IFilterGraph* grafo, LPCWSTR) override
    {
        IFilterGraph* anterior = m_grafo;
        m_grafo = grafo;
        if (m_grafo) m_grafo->AddRef();
        if (anterior) anterior->Release();
        return S_OK;
    }
    STDMETHODIMP QueryVendorInfo(LPWSTR* v) override
    {
        if (!v) return E_POINTER;
        *v = nullptr;
        return E_NOTIMPL;
    }

private:
    ULONG m_refs = 0;
    IPin* m_pinMandante = nullptr;
    AM_MEDIA_TYPE m_mtRecibido = {};
    IFilterGraph* m_grafo = nullptr;
    int m_estado = 0;
};

class TsEnumPinsL : public IEnumPins
{
public:
    TsEnumPinsL(TsFiltro* filtro, int posicion) : m_filtro(filtro), m_pos(posicion) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown) *ppv = static_cast<IUnknown*>(this);
        else if (riid == IID_IEnumPins) *ppv = static_cast<IEnumPins*>(this);
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
    STDMETHODIMP Next(ULONG cPines, IPin** pines, ULONG* obtenidos) override
    {
        if (!pines) return E_POINTER;
        ULONG n = 0;
        while (n < cPines && m_pos < 1)
        {
            pines[n++] = static_cast<IPin*>(m_filtro);
            m_filtro->AddRef();
            ++m_pos;
        }
        if (obtenidos) *obtenidos = n;
        return (n == cPines && n > 0) ? S_OK : S_FALSE;
    }
    STDMETHODIMP Skip(ULONG c) override { m_pos += (int)c; return m_pos < 1 ? S_OK : S_FALSE; }
    STDMETHODIMP Reset() override { m_pos = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumPins** copia) override
    {
        if (!copia) return E_POINTER;
        auto* e = new (std::nothrow) TsEnumPinsL(m_filtro, m_pos);
        if (!e) return E_OUTOFMEMORY;
        *copia = e;
        return S_OK;
    }

private:
    ULONG m_refs = 1;
    TsFiltro* m_filtro;
    int m_pos;
};

STDMETHODIMP TsFiltro::EnumPins(IEnumPins** e)
{
    if (!e) return E_POINTER;
    auto* en = new (std::nothrow) TsEnumPinsL(this, 0);
    if (!en) return E_OUTOFMEMORY;
    *e = en;
    return S_OK;
}

// ---------------------------------------------------------------------------
// Modo body: registro + enumeracion + grafo con sink de pruebas.
// ---------------------------------------------------------------------------
static int ModoBody()
{
    setvbuf(stdout, nullptr, _IONBF, 0);   // sin buffer: ver el avance tras un crash
    wprintf(L"=== body (registro + grafo)\n[t1 inicio]\n");
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    // -- registro -----------------------------------------------------------
    wchar_t valor[MAX_PATH + 4] = L"";
    Marcar(L"CLSID InprocServer32 registrado", LecturaValor(kClaveInproc, valor, sizeof(valor)));
    wprintf(L"       ruta: %s\n", valor);
    Marcar(L"instancia en categoria de camaras", ExisteClave(kClaveInstancia));

    // -- enumeracion como camara --------------------------------------------
    ICreateDevEnum* devEnum = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER,
        IID_ICreateDevEnum, reinterpret_cast<void**>(&devEnum));
    Marcar(L"ICreateDevEnum", hr == S_OK && devEnum);
    if (!devEnum) { CoUninitialize(); return 1; }

    IEnumMoniker* em = nullptr;
    hr = devEnum->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &em, 0);
    Marcar(L"CreateClassEnumerator (VideoInputDeviceCategory)", hr == S_OK && em);
    IBaseFilter* camara = nullptr;
    if (em)
    {
        IMoniker* mk = nullptr;
        int i = 0;
        while (em->Next(1, &mk, nullptr) == S_OK)
        {
            LPOLESTR nombre = nullptr;
            mk->GetDisplayName(nullptr, nullptr, &nombre);
            wprintf(L"       camara[%d]: %s\n", i, nombre ? nombre : L"?");
            ++i;
            if (nombre && wcsstr(nombre, L"Lenscord") != nullptr && !camara)
            {
                hr = mk->BindToObject(nullptr, nullptr, IID_IBaseFilter,
                    reinterpret_cast<void**>(&camara));
                Marcar(L"BindToObject -> filtro Lenscord", hr == S_OK && camara);
            }
            if (nombre) CoTaskMemFree(nombre);
            mk->Release();
        }
        if (!camara) Marcar(L"Lenscord encontrada en la enumeracion", false);
        em->Release();
    }
    devEnum->Release();
    if (!camara) { CoUninitialize(); return 1; }

    // -- grafo --------------------------------------------------------------
    IGraphBuilder* grafo = nullptr;
    hr = CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER,
        IID_IGraphBuilder, reinterpret_cast<void**>(&grafo));
    wprintf(L"[t5 grafo 0x%08X]\n", hr);
    Marcar(L"IGraphBuilder", hr == S_OK && grafo);
    if (!grafo) { camara->Release(); CoUninitialize(); return 1; }

    hr = grafo->AddFilter(camara, L"Lenscord");
    wprintf(L"[t6 addfilter camara 0x%08X]\n", hr);
    Marcar(L"AddFilter (Lenscord)", hr == S_OK);

    IPin* pinCam = nullptr;
    {
        IEnumPins* ep = nullptr;
        if (SUCCEEDED(camara->EnumPins(&ep)) && ep)
        {
            ep->Next(1, &pinCam, nullptr);
            ep->Release();
        }
    }
    Marcar(L"pin de camara", pinCam != nullptr);
    if (!pinCam) { grafo->Release(); camara->Release(); CoUninitialize(); return 1; }

    // --- Sink de pruebas propio (estilo libwebrtc: no entrega allocator) ----
    TsFiltro* sink = new TsFiltro();               // refs internos en 0
    IBaseFilter* sinkFiltro = static_cast<IBaseFilter*>(sink);
    IPin* pinSinkIn = static_cast<IPin*>(sink);
    hr = grafo->AddFilter(sinkFiltro, L"Receptor");
    Marcar(L"AddFilter (TestSink)", hr == S_OK);

    bool conectado = false;
    if (pinSinkIn)
    {
        wprintf(L"[t9 conectando cam -> TestSink]\n");
        hr = grafo->ConnectDirect(pinCam, pinSinkIn, nullptr);
        wprintf(L"[t9b resultado 0x%08X]\n", hr);
        Marcar(L"ConnectDirect camara -> TestSink", hr == S_OK);
        conectado = SUCCEEDED(hr);
    }
    if (conectado)
    {
        // datos del formato negociado (desde el pin de la camara)
        AM_MEDIA_TYPE mtNeg = {};
        if (SUCCEEDED(pinCam->ConnectionMediaType(&mtNeg)))
        {
            if (mtNeg.pbFormat && mtNeg.cbFormat >= sizeof(VIDEOINFOHEADER))
            {
                auto* vih = reinterpret_cast<const VIDEOINFOHEADER*>(mtNeg.pbFormat);
                g_wNeg = vih->bmiHeader.biWidth;
                g_hNeg = vih->bmiHeader.biHeight;
            }
            g_i420Neg = (mtNeg.subtype.Data1 == 0x30323449);
            wprintf(L"       negociado: %s %dx%d (%s)\n", TextoGuid(mtNeg.subtype).c_str(),
                g_wNeg, g_hNeg, g_i420Neg ? L"I420" : L"RGB");
            if (mtNeg.pbFormat) CoTaskMemFree(mtNeg.pbFormat);
        }
    }

    // -- correr y tomar frames ---------------------------------------------
    if (conectado)
    {
        wprintf(L"[t12 corriendo]\n");
        IMediaControl* control = nullptr;
        hr = grafo->QueryInterface(IID_IMediaControl, reinterpret_cast<void**>(&control));
        if (SUCCEEDED(hr) && control)
        {
            hr = control->Run();
            wprintf(L"[t13 run 0x%08X]\n", hr);
            Marcar(L"IMediaControl::Run", hr == S_OK || hr == S_FALSE);
            Sleep(1500);
            wprintf(L"[t14 mediciones]\n");

            // diagnostico del worker dentro de la DLL
            typedef const LONG* (__stdcall* FnDiag)();
            HMODULE mDiag = GetModuleHandleW(L"Lenscord.VirtualCamera.DShow.dll");
            if (mDiag)
            {
                auto* diag = reinterpret_cast<FnDiag>(GetProcAddress(mDiag, "LenscordDiag"));
                if (diag)
                {
                    const LONG* d = diag();
                    const LONG rondas = d[13];
                    wprintf(L"       [diag] hilo=%ld iter=%ld conect=%ld hrGetBuf=%ld fallosGetBuf=%ld okGetBuf=%ld hrRecv=%ld recv=%ld\n",
                        d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7]);
                    if (rondas > 0)
                    {
                        wprintf(L"       [diag] %ld ciclos: ciclo=%ld ms copia=%ld ms getbuf=%ld ms conv=%ld ms recv=%ld ms\n",
                            rondas, d[8] / rondas, d[11] / rondas, d[12] / rondas, d[9] / rondas, d[10] / rondas);
                        wprintf(L"       [diag] esperas: externa=%ld ms ritmo=%ld ms (por iter=%ld ms)\n",
                            d[14], d[15], (d[14] + d[15] + d[8]) / (rondas > 0 ? rondas : 1));
                    }
                    // ventana temporal del bucle segun QPC crudo (aritmetica
                    // sin signo para tolerar el wrap de LowPart)
                    LARGE_INTEGER fQ;
                    QueryPerformanceFrequency(&fQ);
                    {
                        const ULONG delta = (ULONG)((ULONG)d[17] - (ULONG)d[16]);
                        const LONG ventanaMs = static_cast<LONG>(delta * 1000ULL / fQ.QuadPart);
                        if (d[16] != 0 && delta < 0x80000000UL)
                        {
                            const LONG porIter = (d[1] > 1) ? ventanaMs / (d[1] - 1) : 0;
                            wprintf(L"       [diag] ventana bucle: %ld ms (%ld iter) = %ld ms/iter; cola post-recv: %ld ms\n",
                                ventanaMs, d[1], porIter, d[18] ? (LONG)((ULONG)((ULONG)d[17] - (ULONG)d[18]) * 1000ULL / fQ.QuadPart) : -1);
                        }
                    }
                }
            }

            wprintf(L"       samples recibidos: %ld (1.5 s a 30 fps esperaria ~45)\n", ts_recibidos);
            Marcar(L"frames entregados al sink", ts_recibidos >= 20);
            wprintf(L"       NotifyAllocator: %s\n",
                ts_allocExterno ? L"allocator externo" : L"allocator propio del filtro (MemAlloc)");

            const long cbEsperado = g_i420Neg
                ? static_cast<long>(g_wNeg) * g_hNeg * 3 / 2
                : static_cast<long>(g_wNeg) * g_hNeg * 4;
            wprintf(L"       longitud del ultimo sample: %ld (esperado %ld)\n", ts_ultLen, cbEsperado);
            Marcar(L"longitud de datos del sample", ts_ultLen == cbEsperado);

            if (ts_ultFrame.size() == static_cast<size_t>(cbEsperado) && ts_previoFrame.size() == ts_ultFrame.size())
            {
                const unsigned char* plano = ts_ultFrame.data();
                const int tamanoPlano = g_wNeg * g_hNeg;
                long minimo = 255, maximo = 0;
                for (int i = 0; i < tamanoPlano; ++i)
                {
                    if (plano[i] < minimo) minimo = plano[i];
                    if (plano[i] > maximo) maximo = plano[i];
                }
                wprintf(L"       plano Y: min=%ld max=%ld\n", minimo, maximo);
                long cambios = 0;
                const unsigned char* previo = ts_previoFrame.data();
                for (int i = 0; i < tamanoPlano; ++i)
                    if (abs((int)plano[i] - (int)previo[i]) > 4) ++cambios;
                wprintf(L"       cambio entre los dos ultimos frames: %ld pixeles\n", cambios);
                wprintf(L"       contenido: %s\n",
                    cambios > 0 ? L"EN MOVIMIENTO (publicador vivo)"
                                : L"estatico (publicador apagado o standby)");
            }
            else
            {
                wprintf(L"       sin dos frames locales para comparar (standby?)\n");
            }
            control->Stop();
            control->Release();
        }
    }
    else
    {
        Marcar(L"conexion del grafo", false);
    }

    if (pinCam) pinCam->Release();
    if (sinkFiltro)
    {
        grafo->RemoveFilter(sinkFiltro);   // el grafo suelta su referencia
        sinkFiltro->Release();            // ref del dueno -> refs 0 -> delete
    }
    grafo->Release();
    camara->Release();
    CoUninitialize();

    wprintf(L"=== body: %d/%d pruebas correctas\n", Ok, Total);
    return Fallos ? 1 : 0;
}

// ---------------------------------------------------------------------------
// Modo render: replica el orden real de libwebrtc/Discord:
// (1) SetFiltergraph + RenderStream(PIN_CATEGORY_CAPTURE) -> conecta primero
// (2) IAMStreamConfig::SetFormat en el pin DESPUES de conectar (720p/otro)
// (3) Run y contar muestras con el longitud que pide el SetFormat.
// ---------------------------------------------------------------------------
static void DeleteMediaType_(AM_MEDIA_TYPE* mt)
{
    if (!mt) return;
    if (mt->pbFormat) CoTaskMemFree(mt->pbFormat);
    if (mt->pUnk) mt->pUnk->Release();
    CoTaskMemFree(mt);
}

static const GUID MiSubtipoI420 = { 0x30323449, 0x0000, 0x0010,
    { 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 } };

static GUID SubtipoDeIndice(int formato)
{
    if (formato == 1) return MEDIASUBTYPE_RGB24;
    if (formato == 2) return MEDIASUBTYPE_RGB32;
    return MiSubtipoI420;
}

static AM_MEDIA_TYPE MtConstruido(int formato, int w, int h, REFERENCE_TIME promedio)
{
    const GUID subtipo = SubtipoDeIndice(formato);
    const long bytes = (formato == 0) ? w * h * 3 / 2 : (formato == 1) ? w * h * 3 : w * h * 4;

    static VIDEOINFOHEADER vih = {};   // llamadas secuenciales en un hilo
    ZeroMemory(&vih, sizeof(vih));
    vih.AvgTimePerFrame = promedio;
    vih.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    vih.bmiHeader.biWidth = w;
    vih.bmiHeader.biHeight = h;
    vih.bmiHeader.biPlanes = 1;
    vih.bmiHeader.biBitCount = (formato == 0) ? 12 : (formato == 1) ? 24 : 32;
    vih.bmiHeader.biCompression = (formato == 0) ? 'I420' : BI_RGB;
    vih.bmiHeader.biSizeImage = bytes;

    AM_MEDIA_TYPE mt = {};
    mt.majortype = MEDIATYPE_Video;
    mt.subtype = subtipo;
    mt.formattype = FORMAT_VideoInfo;
    mt.bFixedSizeSamples = TRUE;
    mt.bTemporalCompression = FALSE;
    mt.lSampleSize = bytes;
    mt.cbFormat = sizeof(vih);
    mt.pbFormat = reinterpret_cast<BYTE*>(&vih);
    return mt;
}

static int ModoRender()
{
    setvbuf(stdout, nullptr, _IONBF, 0);
    wprintf(L"=== render (flujo libwebrtc/Discord)\n");
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    // enumeracion -> camera
    ICreateDevEnum* devEnum = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER,
        IID_ICreateDevEnum, reinterpret_cast<void**>(&devEnum));
    if (FAILED(hr) || !devEnum) { CoUninitialize(); return 1; }
    IEnumMoniker* em = nullptr;
    devEnum->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &em, 0);
    IBaseFilter* camara = nullptr;
    if (em)
    {
        IMoniker* mk = nullptr;
        while (em->Next(1, &mk, nullptr) == S_OK)
        {
            LPOLESTR nombre = nullptr;
            mk->GetDisplayName(nullptr, nullptr, &nombre);
            if (nombre && wcsstr(nombre, L"Lenscord") != nullptr && !camara)
                mk->BindToObject(nullptr, nullptr, IID_IBaseFilter,
                    reinterpret_cast<void**>(&camara));
            if (nombre) CoTaskMemFree(nombre);
            mk->Release();
        }
        em->Release();
    }
    devEnum->Release();
    Marcar(L"enumeracion encontro Lenscord", camara != nullptr);
    if (!camara) { CoUninitialize(); return 1; }

    IGraphBuilder* grafo = nullptr;
    hr = CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER,
        IID_IGraphBuilder, reinterpret_cast<void**>(&grafo));
    Marcar(L"IGraphBuilder", hr == S_OK && grafo);
    if (!grafo) { camara->Release(); CoUninitialize(); return 1; }

    ICaptureGraphBuilder2* cgr = nullptr;
    hr = CoCreateInstance(CLSID_CaptureGraphBuilder2, nullptr, CLSCTX_INPROC_SERVER,
        IID_ICaptureGraphBuilder2, reinterpret_cast<void**>(&cgr));
    Marcar(L"ICaptureGraphBuilder2", hr == S_OK && cgr);
    if (cgr) { hr = cgr->SetFiltergraph(grafo); Marcar(L"SetFiltergraph", hr == S_OK); }

    hr = grafo->AddFilter(camara, L"Lenscord");
    Marcar(L"AddFilter (Lenscord)", hr == S_OK);

    TsFiltro* sink = new TsFiltro();
    IBaseFilter* sinkFiltro = static_cast<IBaseFilter*>(sink);
    hr = grafo->AddFilter(sinkFiltro, L"Receptor");
    Marcar(L"AddFilter (TestSink)", hr == S_OK);

    // (1) RenderStream: como libwebrtc une los pines
    hr = cgr->RenderStream(&PIN_CATEGORY_CAPTURE, &MEDIATYPE_Video, camara, nullptr, sinkFiltro);
    wprintf(L"[render-stream 0x%08X]\n", hr);
    Marcar(L"RenderStream PIN_CATEGORY_CAPTURE", hr == S_OK);
    if (FAILED(hr)) { sinkFiltro->Release(); grafo->Release(); camara->Release(); cgr->Release(); CoUninitialize(); return 1; }

    // pin + IAMStreamConfig (como libwebrtc lo hace en el pin de la fuente)
    IPin* pinCam = nullptr;
    {
        IEnumPins* ep = nullptr;
        if (SUCCEEDED(camara->EnumPins(&ep)) && ep) { ep->Next(1, &pinCam, nullptr); ep->Release(); }
    }
    IAMStreamConfig* sc = nullptr;
    if (pinCam)
        pinCam->QueryInterface(IID_IAMStreamConfig, reinterpret_cast<void**>(&sc));
    Marcar(L"IAMStreamConfig en el pin", sc != nullptr);
    if (pinCam) pinCam->Release();
    AM_MEDIA_TYPE* mtConectado = nullptr;
    if (sc) sc->GetFormat(&mtConectado);
    if (mtConectado)
    {
        auto* vih = reinterpret_cast<const VIDEOINFOHEADER*>(mtConectado->pbFormat);
        wprintf(L"       conectado via RenderStream: %s %dx%d\n",
            TextoGuid(mtConectado->subtype).c_str(),
            vih ? vih->bmiHeader.biWidth : 0, vih ? vih->bmiHeader.biHeight : 0);
        DeleteMediaType_(mtConectado);
    }

    // correr
    IMediaControl* control = nullptr;
    hr = grafo->QueryInterface(IID_IMediaControl, reinterpret_cast<void**>(&control));
    if (SUCCEEDED(hr) && control) control->Run();

    // (2) SetFormat despues de conectar: caso A (mismo subtipo, otras dims,
    // AvgTimePerFrame=0 como arma el mt libwebrtc)
    InterlockedExchange(&ts_recibidos, 0);
    InterlockedExchange(&ts_ultLen, 0);
    static const GUID MiSubtipoI420 = { 0x30323449, 0x0000, 0x0010,
        { 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 } };
    AM_MEDIA_TYPE mtA = MtConstruido(0, 640, 480, 0);
    hr = E_FAIL;
    if (sc) hr = sc->SetFormat(&mtA);
    wprintf(L"[render setformat I420 640x480 -> 0x%08X]\n", hr);
    Marcar(L"SetFormat post-conexion (I420 640x480)", hr == S_OK);
    Sleep(1200);
    const long esperadoA = 640 * 480 * 3 / 2;
    wprintf(L"       samples: %ld | longitud ultima: %ld (esperado %ld)\n",
        ts_recibidos, ts_ultLen, esperadoA);
    Marcar(L"muestra I420 640x480 entregada", ts_recibidos >= 15 && ts_ultLen == esperadoA);

    // caso B: cambiar de subtipo y tamano con el grafo corriendo
    InterlockedExchange(&ts_recibidos, 0);
    InterlockedExchange(&ts_ultLen, 0);
    AM_MEDIA_TYPE mtB = MtConstruido(1, 320, 240, 0);
    hr = E_FAIL;
    if (sc) hr = sc->SetFormat(&mtB);
    wprintf(L"[render setformat RGB24 320x240 -> 0x%08X]\n", hr);
    Marcar(L"SetFormat post-conexion (RGB24 320x240)", hr == S_OK);
    Sleep(1000);
    const long esperadoB = 320 * 240 * 3;
    wprintf(L"       samples: %ld | longitud ultima: %ld (esperado %ld)\n",
        ts_recibidos, ts_ultLen, esperadoB);
    Marcar(L"muestra RGB24 320x240 entregada", ts_recibidos >= 10 && ts_ultLen == esperadoB);

    if (control)
    {
        control->Stop();
        control->Release();
    }
    if (sc) sc->Release();
    sinkFiltro->Release();
    grafo->Release();
    camara->Release();
    cgr->Release();
    CoUninitialize();

    wprintf(L"=== render: %d/%d pruebas correctas\n", Ok, Total);
    return Fallos ? 1 : 0;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 3 && wcscmp(argv[1], L"factory") == 0)
        return ModoFactory(argv[2]);
    if (argc >= 2 && wcscmp(argv[1], L"body") == 0)
        return ModoBody();
    if (argc >= 2 && wcscmp(argv[1], L"render") == 0)
        return ModoRender();
    wprintf(L"uso: test-dshow.exe factory <ruta\\dll> | body | render\n");
    return 2;
}
