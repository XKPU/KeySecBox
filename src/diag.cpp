#include "internal.h"

#include <windows.h>
#include <string>
#include <mutex>
#include <cstdarg>

// 诊断日志：仅 s.diag 开启时追加写 <base>.diag.log。
// 只记录操作名/id/计数/返回码等非机密信息，严禁写入主密码、账号、密码、备注、恢复密钥。

namespace {

std::mutex g_diagMutex;

std::string wstr_to_utf8(const std::wstring& w)
{
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string out(static_cast<size_t>(n) - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, &out[0], n, nullptr, nullptr);
    return out;
}

} // namespace

void diag_log(const ksbx_store& s, const char* fmt, ...)
{
    if (!s.diag || s.basePath.empty()) return;

    std::lock_guard<std::mutex> lock(g_diagMutex);

    SYSTEMTIME st;
    GetLocalTime(&st);
    char ts[64];
    snprintf(ts, sizeof(ts), "[%04u-%02u-%02u %02u:%02u:%02u.%03u] ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    char msg[4096];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof(msg), fmt, ap);
    va_end(ap);
    msg[sizeof(msg) - 1] = '\0';

    std::wstring path = s.basePath + L".diag.log";
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"ab") != 0 || !f) return;
    fputs(ts, f);
    fputs(msg, f);
    fputc('\n', f);
    fclose(f);
}
