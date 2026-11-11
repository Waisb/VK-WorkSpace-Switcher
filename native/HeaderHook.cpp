// Runs only on the selected application's GUI thread through a thread-local
// WH_CALLWNDPROC hook. No executable patching, remote threads or Qt SDK needed.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cwchar>
#include <string>
#include <vector>

namespace {
constexpr ULONG_PTR kMessage = 0x564B4852;
constexpr wchar_t kResult[] = L"VKWorkspaceSwitcher.HeaderResult";
struct Packet {
    uint32_t version;
    uint32_t request;
    uint32_t color;
    wchar_t title[81];
};
// Qt 6 QArrayDataPointer layout. Used only for the explicitly checked Qt ABI.
struct QtString { void* allocation; const wchar_t* data; int64_t size; };
struct QtChildren { void* allocation; void** data; int64_t size; };
static_assert(sizeof(QtString) == 24 && sizeof(QtChildren) == 24, "Qt 6 x64 layout required");
static_assert(sizeof(Packet) == 176, "Managed/native header packet layout mismatch");
using GetString = void* (__fastcall*)(void*, QtString*);

struct QtApi {
    void* (__fastcall* find)(uintptr_t);
    const QtChildren* (__fastcall* children)(void*);
    bool (__fastcall* inherits)(void*, const char*);
    void* (__fastcall* parent)(void*);
    GetString objectName, labelText, styleSheet;
    void* (__fastcall* fromUtf16)(QtString*, const wchar_t*, int64_t);
    void (__fastcall* clear)(QtString*);
    void (__fastcall* setObjectName)(void*, const QtString*);
    void (__fastcall* setStyleSheet)(void*, const QtString*);
    void (__fastcall* setText)(void*, const QtString*);
    void (__fastcall* setTextFormat)(void*, int);
    int (__fastcall* height)(void*);
    void (__fastcall* update)(void*);
    void (__fastcall* setAttribute)(void*, int, bool);
    const char* (__cdecl* version)();

    bool load() {
        HMODULE core = GetModuleHandleW(L"Qt6Core.dll");
        HMODULE widgets = GetModuleHandleW(L"Qt6Widgets.dll");
        if (!core || !widgets) return false;
#define GET(field, module, symbol) field = reinterpret_cast<decltype(field)>(GetProcAddress(module, symbol)); if (!field) return false
        GET(find, widgets, "?find@QWidget@@SAPEAV1@_K@Z");
        GET(children, core, "?children@QObject@@QEBAAEBV?$QList@PEAVQObject@@@@XZ");
        GET(inherits, core, "?inherits@QObject@@QEBA_NPEBD@Z");
        GET(parent, core, "?parent@QObject@@QEBAPEAV1@XZ");
        GET(objectName, core, "?objectName@QObject@@QEBA?AVQString@@XZ");
        GET(labelText, widgets, "?text@QLabel@@QEBA?AVQString@@XZ");
        GET(styleSheet, widgets, "?styleSheet@QWidget@@QEBA?AVQString@@XZ");
        GET(fromUtf16, core, "?fromUtf16@QString@@SA?AV1@PEBG_J@Z");
        GET(clear, core, "?clear@QString@@QEAAXXZ");
        GET(setObjectName, core, "?setObjectName@QObject@@QEAAXAEBVQString@@@Z");
        GET(setStyleSheet, widgets, "?setStyleSheet@QWidget@@QEAAXAEBVQString@@@Z");
        GET(setText, widgets, "?setText@QLabel@@QEAAXAEBVQString@@@Z");
        GET(setTextFormat, widgets, "?setTextFormat@QLabel@@QEAAXW4TextFormat@Qt@@@Z");
        GET(height, widgets, "?height@QWidget@@QEBAHXZ");
        GET(update, widgets, "?update@QWidget@@QEAAXXZ");
        GET(setAttribute, widgets, "?setAttribute@QWidget@@QEAAXW4WidgetAttribute@Qt@@_N@Z");
        GET(version, core, "qVersion");
#undef GET
        return true;
    }

    std::wstring read(void* object, GetString getter) {
        QtString text{};
        getter(object, &text); // MSVC member return: RCX=this, RDX=result.
        std::wstring result;
        if (text.data && text.size > 0 && text.size < 1024 * 1024)
            result.assign(text.data, static_cast<size_t>(text.size));
        clear(&text);
        return result;
    }

    void write(void* object, void (__fastcall* setter)(void*, const QtString*), const std::wstring& value) {
        QtString text{};
        fromUtf16(&text, value.data(), static_cast<int64_t>(value.size()));
        setter(object, &text);
        clear(&text);
    }
};

bool findCaption(QtApi& qt, void* root, void*& caption, void*& header, int depth, int& visited) {
    if (!root || depth > 5 || ++visited > 600) return false;
    if (qt.inherits(root, "QLabel")) {
        const auto name = qt.read(root, qt.objectName);
        void* candidate = qt.parent(root);
        if (candidate && qt.inherits(candidate, "QWidget") && qt.height(candidate) >= 20 && qt.height(candidate) <= 64) {
            bool logo = false;
            const QtChildren* siblings = qt.children(candidate);
            if (siblings && siblings->size >= 0 && siblings->size < 100) {
                for (int64_t i = 0; i < siblings->size; ++i)
                    if (qt.read(siblings->data[i], qt.objectName) == L"windowIcon") { logo = true; break; }
            }
            if (logo && (name == L"vkwsProfileCaption" || qt.read(root, qt.labelText) == L"VK WorkSpace")) {
                caption = root; header = candidate; return true;
            }
        }
    }
    const QtChildren* children = qt.children(root);
    if (!children || children->size < 0 || children->size > 2000) return false;
    // Copy pointers before traversing; applying is done only after traversal ends.
    std::vector<void*> copy(children->data, children->data + children->size);
    for (void* child : copy)
        if (findCaption(qt, child, caption, header, depth + 1, visited)) return true;
    return false;
}

int apply(HWND window, const Packet& packet) {
    QtApi qt{};
    if (!qt.load()) return 2;
    if (std::string(qt.version()) != "6.8.3") return 3;
    void* root = qt.find(reinterpret_cast<uintptr_t>(window));
    if (!root) return 4;
    void* caption = nullptr;
    void* header = nullptr;
    int visited = 0;
    if (!findCaption(qt, root, caption, header, 0, visited)) return 5;

    const auto title = std::wstring(packet.title);
    if (qt.read(caption, qt.objectName) != L"vkwsProfileCaption") qt.write(caption, qt.setObjectName, L"vkwsProfileCaption");
    qt.setTextFormat(caption, 0); // Qt::PlainText; profile names are never markup.
    if (qt.read(caption, qt.labelText) != title) qt.write(caption, qt.setText, title);

    wchar_t color[8];
    swprintf_s(color, L"#%06X", packet.color & 0xffffff);
    const int r = (packet.color >> 16) & 255, g = (packet.color >> 8) & 255, b = packet.color & 255;
    const wchar_t* ink = (r * 299 + g * 587 + b * 114 >= 155000) ? L"#101318" : L"#FFFFFF";
    std::wstring old = qt.read(header, qt.styleSheet);
    std::wstring base = old;
    const auto begin = base.find(L"/* VKWS HEADER BEGIN */");
    const auto end = begin == std::wstring::npos ? std::wstring::npos : base.find(L"/* VKWS HEADER END */", begin);
    if (begin != std::wstring::npos && end != std::wstring::npos)
        base.erase(begin, end + wcslen(L"/* VKWS HEADER END */") - begin);
    while (!base.empty() && (base.back() == L'\n' || base.back() == L'\r')) base.pop_back();
    std::wstring css = base + L"\n/* VKWS HEADER BEGIN */\n* { background-color: " + color + L"; color: " + ink +
        L"; } QLabel { color: " + ink +
        L"; background: transparent; }\n/* VKWS HEADER END */";
    if (old != css) qt.write(header, qt.setStyleSheet, css);
    qt.setAttribute(header, 93, true); // Qt::WA_StyledBackground
    qt.update(header);
    return 1;
}

int safeApply(HWND window, const Packet* packet) {
    __try { return apply(window, *packet); }
    __except(EXCEPTION_EXECUTE_HANDLER) { return 7; }
}
}

extern "C" __declspec(dllexport) LRESULT CALLBACK VkWorkspaceHeaderHook(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0 && lParam) {
        auto* message = reinterpret_cast<CWPSTRUCT*>(lParam);
        if (message->message == WM_COPYDATA && message->lParam) {
            auto* data = reinterpret_cast<COPYDATASTRUCT*>(message->lParam);
            if (data->dwData == kMessage && data->cbData == sizeof(Packet) && data->lpData) {
                const auto* packet = static_cast<const Packet*>(data->lpData);
                if (packet->version == 1 && packet->title[80] == 0) {
                    const int result = safeApply(message->hwnd, packet);
                    const ULONG_PTR value = (static_cast<ULONG_PTR>(packet->request) << 8) | result;
                    SetPropW(message->hwnd, kResult, reinterpret_cast<HANDLE>(value));
                }
            }
        }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}
