#include "internal.h"

#include <windows.h>
#include <string>
#include <vector>
#include <unordered_map>
#include <map>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <cwctype>

using namespace ksbx::json;

#pragma region 加密流

bool file_exists(const std::wstring& path)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"rb") == 0 && f) { fclose(f); return true; }
    return false;
}

bool encrypt_blob(ksbx_store& s, const std::string& plain,
                  std::vector<uint8_t>& out_nonce, std::vector<uint8_t>& out_blob)
{
    std::vector<uint8_t> p(plain.begin(), plain.end());
    return s.gcm.encrypt(p, out_nonce, out_blob);
}

bool decrypt_blob(ksbx_store& s, const std::vector<uint8_t>& nonce,
                  const std::vector<uint8_t>& blob, std::string& out_plain)
{
    std::vector<uint8_t> plain;
    if (!s.gcm.decrypt(nonce, blob, plain)) return false;
    out_plain.assign(plain.begin(), plain.end());
    std::fill(plain.begin(), plain.end(), 0); // 抹除瞬态明文
    return true;
}

// 构建单条 data 记录（含 id 头）。sc 为空则从 dataFile 原样拷贝旧密文（未改动项不解密）。
std::vector<uint8_t> build_secret_record(ksbx_store& s, long long id, const SecretCache* sc)
{
    std::vector<uint8_t> rec;
    if (sc) {
        std::string secPlain = serialize_secret(sc->account, sc->password, sc->note);
        std::vector<uint8_t> sNonce, sBlob;
        if (!encrypt_blob(s, secPlain, sNonce, sBlob)) return {};
        put_i64(rec, id);
        rec.insert(rec.end(), sNonce.begin(), sNonce.end());
        put_u32(rec, (uint32_t)sBlob.size());
        rec.insert(rec.end(), sBlob.begin(), sBlob.end());
    } else {
        auto locIt = s.dataLoc.find(id);
        if (locIt == s.dataLoc.end() || s.dataFile.empty()) return {};
        const auto& loc = locIt->second;
        if (loc.offset + loc.total > s.dataFile.size()) return {};
        rec.assign(s.dataFile.begin() + loc.offset,
                   s.dataFile.begin() + loc.offset + loc.total);
    }
    return rec;
}

#pragma endregion

#pragma region settings

bool write_settings(ksbx_store& s)
{
    std::vector<uint8_t> blob;
    blob.push_back('K'); blob.push_back('S'); blob.push_back('X'); blob.push_back('3');
    put_u32(blob, 2); // 格式版本 2（KDF 类型 + 扩展设置）
    for (uint8_t x : s.salt) blob.push_back(x);
    blob.push_back(KDF_PBKDF2);
    put_u32(blob, s.iterations);

    std::vector<uint8_t> cNonce, cBlob;
    if (!encrypt_blob(s, "KSX3-OK", cNonce, cBlob)) return false;
    for (uint8_t x : cNonce) blob.push_back(x);
    put_u32(blob, (uint32_t)cBlob.size());
    for (uint8_t x : cBlob) blob.push_back(x);

    // 扩展设置 JSON（明文、非机密）：墓碑上限 + 诊断开关
    char ebuf[128];
    snprintf(ebuf, sizeof(ebuf),
             "{\"tombMaxBytes\":%u,\"tombMaxCount\":%u,\"diag\":%d}",
             s.tombMaxBytes, s.tombMaxCount, s.diag ? 1 : 0);
    for (char c : std::string(ebuf)) blob.push_back((uint8_t)c);

    // 先写临时文件再替换，避免写坏承载盐/KDF 的 settings（否则新旧密码都无法开库）
    std::wstring tmp = s.settingsPath + L".tmp";
    FILE* f = nullptr;
    if (_wfopen_s(&f, tmp.c_str(), L"wb") != 0 || !f) return false;
    size_t w = fwrite(blob.data(), 1, blob.size(), f);
    fclose(f);
    if (w != blob.size()) return false;
    if (MoveFileExW(tmp.c_str(), s.settingsPath.c_str(), MOVEFILE_REPLACE_EXISTING) == 0)
        return false;
    diag_log(s, "write_settings: bytes=%zu diag=%d", blob.size(), s.diag ? 1 : 0);
    return true;
}

int load_settings(ksbx_store& s)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, s.settingsPath.c_str(), L"rb") != 0 || !f) return KSBOX_ERR_NO_VAULT;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 40) { fclose(f); return KSBOX_ERR_IO; }
    std::vector<uint8_t> blob((size_t)sz);
    size_t r = fread(blob.data(), 1, sz, f);
    fclose(f);
    if (r != (size_t)sz) return KSBOX_ERR_IO;

    if (blob[0] != 'K' || blob[1] != 'S' || blob[2] != 'X' || blob[3] != '3')
        return KSBOX_ERR_IO;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(blob, p, ver)) return KSBOX_ERR_IO;
    if (p + 16 > blob.size()) return KSBOX_ERR_IO;
    s.salt.assign(blob.begin() + p, blob.begin() + p + 16); p += 16;
    if (ver >= 2) {
        uint8_t kdf = 0;
        if (!get_u8(blob, p, kdf)) return KSBOX_ERR_IO;
        if (kdf != KDF_PBKDF2) return KSBOX_ERR_IO; // 不支持其他 KDF
        if (!get_u32(blob, p, s.iterations)) return KSBOX_ERR_IO;
    } else {
        if (!get_u32(blob, p, s.iterations)) return KSBOX_ERR_IO;
    }
    std::vector<uint8_t> cNonce, cBlob;
    if (!get_bytes(blob, p, 12, cNonce)) return KSBOX_ERR_IO;
    uint32_t cLen = 0;
    if (!get_u32(blob, p, cLen)) return KSBOX_ERR_IO;
    if (!get_bytes(blob, p, cLen, cBlob)) return KSBOX_ERR_IO;
    s.chkNonce = std::move(cNonce);
    s.chkBlob = std::move(cBlob);
    // ver>=2 末尾有扩展设置 JSON
    if (ver >= 2 && p < blob.size()) {
        std::string extJson(blob.begin() + p, blob.end());
        bool ok = false;
        Value ext = parse(extJson, ok);
        if (ok && ext.type == Value::Obj) {
            s.tombMaxBytes = (uint32_t)get_i64(ext, "tombMaxBytes");
            s.tombMaxCount = (uint32_t)get_i64(ext, "tombMaxCount");
            s.diag = (get_i64(ext, "diag") != 0);
        }
    }
    diag_log(s, "load_settings: ver=%u iterations=%u diag=%d", ver, s.iterations, s.diag ? 1 : 0);
    return KSBOX_OK;
}

// 校验密码：用已派生的 s.key 解密 settings 校验块（密码错误则 GCM tag 失败）
bool verify_password(ksbx_store& s)
{
    std::string chk;
    if (!decrypt_blob(s, s.chkNonce, s.chkBlob, chk)) return false;
    return chk == "KSX3-OK";
}

// 按 salt+KDF 参数派生密钥并初始化 GCM 会话
bool derive_for_store(ksbx_store& s, const std::wstring& masterPwd)
{
    s.key.assign(32, 0);
    if (!ksbx::crypto::derive_key(masterPwd, s.salt, s.iterations, s.key)) return false;
    return s.gcm.init(s.key);
}

#pragma endregion

#pragma region index

bool write_index(ksbx_store& s)
{
    // index 为明文（分类/备注/元信息非机密）：magic + ver + JSON
    std::string plain = serialize_index(s);
    std::vector<uint8_t> out;
    out.push_back('K'); out.push_back('S'); out.push_back('X'); out.push_back('I');
    put_u32(out, 1);
    for (char c : plain) out.push_back((uint8_t)c);

    // 先写临时文件再替换，避免崩溃留下半截 index
    std::wstring tmp = s.indexPath + L".tmp";
    FILE* f = nullptr;
    if (_wfopen_s(&f, tmp.c_str(), L"wb") != 0 || !f) return false;
    size_t w = fwrite(out.data(), 1, out.size(), f);
    fclose(f);
    if (w != out.size()) return false;
    if (MoveFileExW(tmp.c_str(), s.indexPath.c_str(), MOVEFILE_REPLACE_EXISTING) == 0)
        return false;
    s.indexDirty = false;
    diag_log(s, "write_index: bytes=%zu", out.size());
    return true;
}

int load_index(ksbx_store& s)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, s.indexPath.c_str(), L"rb") != 0 || !f) return KSBOX_ERR_IO;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 8) { fclose(f); return KSBOX_ERR_IO; }
    std::vector<uint8_t> blob((size_t)sz);
    size_t r = fread(blob.data(), 1, sz, f);
    fclose(f);
    if (r != (size_t)sz) return KSBOX_ERR_IO;

    if (blob[0] != 'K' || blob[1] != 'S' || blob[2] != 'X' || blob[3] != 'I')
        return KSBOX_ERR_IO;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(blob, p, ver)) return KSBOX_ERR_IO;
    if (ver != 1) return KSBOX_ERR_IO;
    std::string text(blob.begin() + p, blob.end());
    if (!deserialize_index(s, text)) return KSBOX_ERR_IO;
    diag_log(s, "load_index: OK bytes=%zu", blob.size());
    return KSBOX_OK;
}

#pragma endregion

#pragma region data

// data 文件：仅追加 secretCache 改动项（增量写），并同步更新 dataLoc/dataFile。
bool write_data(ksbx_store& s)
{
    if (s.secretCache.empty()) return true; // 无改动

    bool exists = file_exists(s.dataPath);
    FILE* f = nullptr;
    if (exists) {
        if (_wfopen_s(&f, s.dataPath.c_str(), L"ab") != 0 || !f) return false;
    } else {
        if (_wfopen_s(&f, s.dataPath.c_str(), L"wb") != 0 || !f) return false;
        const uint8_t hdr[8] = { 'K','S','X','D', 2,0,0,0 };
        if (fwrite(hdr, 1, 8, f) != 8) { fclose(f); return false; }
        s.dataFile.assign(hdr, hdr + 8);
    }

    fseek(f, 0, SEEK_END);
    uint64_t offset = (uint64_t)ftell(f);

    for (const auto& kv : s.secretCache) {
        std::vector<uint8_t> rec = build_secret_record(s, kv.first, &kv.second);
        if (rec.empty()) { fclose(f); return false; }
        size_t w = fwrite(rec.data(), 1, rec.size(), f);
        if (w != rec.size()) { fclose(f); return false; }
        DataLoc loc; loc.offset = offset; loc.total = (uint32_t)rec.size();
        s.dataLoc[kv.first] = loc;
        s.dataFile.insert(s.dataFile.end(), rec.begin(), rec.end());
        offset += rec.size();
    }
    fclose(f);
    diag_log(s, "write_data: appended=%zu totalBytes=%zu", s.secretCache.size(), s.dataFile.size());
    return true;
}

// 从头重建 data（初始设置/换密码/压缩时使用），跳过墓碑标记的 id。
bool rebuild_data(ksbx_store& s)
{
    std::vector<uint8_t> out;
    const uint8_t hdr[8] = { 'K','S','X','D', 2,0,0,0 };
    out.insert(out.end(), hdr, hdr + 8);
    // 不能先清空 dataLoc：未改动条目的密文要从旧 dataFile 按 dataLoc 原样拷贝。
    // 新定位先收集到 newLoc，全部构建成功后才替换。
    std::unordered_map<long long, DataLoc> newLoc;

    for (const auto& kv : s.metas) {
        SecretCache* sc = nullptr;
        auto c = s.secretCache.find(kv.first);
        if (c != s.secretCache.end()) sc = &c->second;
        std::vector<uint8_t> rec = build_secret_record(s, kv.first, sc);
        if (rec.empty()) return false;
        DataLoc loc; loc.offset = (uint64_t)out.size(); loc.total = (uint32_t)rec.size();
        newLoc[kv.first] = loc;
        out.insert(out.end(), rec.begin(), rec.end());
    }

    std::wstring tmp = s.dataPath + L".tmp";
    FILE* f = nullptr;
    if (_wfopen_s(&f, tmp.c_str(), L"wb") != 0 || !f) return false;
    size_t w = fwrite(out.data(), 1, out.size(), f);
    fclose(f);
    if (w != out.size()) return false;
    if (MoveFileExW(tmp.c_str(), s.dataPath.c_str(), MOVEFILE_REPLACE_EXISTING) == 0)
        return false;
    s.dataFile = std::move(out);
    s.dataLoc = std::move(newLoc);
    diag_log(s, "rebuild_data: bytes=%zu entries=%zu", s.dataFile.size(), s.dataLoc.size());
    return true;
}

int load_data(ksbx_store& s)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, s.dataPath.c_str(), L"rb") != 0 || !f) return KSBOX_ERR_IO;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 8) { fclose(f); return KSBOX_ERR_IO; }
    std::vector<uint8_t> blob((size_t)sz);
    size_t r = fread(blob.data(), 1, sz, f);
    fclose(f);
    if (r != (size_t)sz) return KSBOX_ERR_IO;

    if (blob[0] != 'K' || blob[1] != 'S' || blob[2] != 'X' || blob[3] != 'D')
        return KSBOX_ERR_IO;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(blob, p, ver)) return KSBOX_ERR_IO;
    if (ver != 2) return KSBOX_ERR_IO;

    // 记录每条 id 当前有效密文位置（同 id 仅保留最后一条）
    std::unordered_map<long long, DataLoc> latest;
    while (p < blob.size()) {
        long long id = 0;
        std::vector<uint8_t> nonce;
        uint32_t len = 0;
        if (!get_i64(blob, p, id)) break;
        if (!get_bytes(blob, p, 12, nonce)) break;
        if (!get_u32(blob, p, len)) break;
        uint64_t recStart = (uint64_t)(p - 8 - 12 - 4); // id 起始偏移
        if (!get_bytes(blob, p, len, nonce)) break; // len 已含 16 字节 tag
        DataLoc loc;
        loc.offset = recStart;
        loc.total = 8u + 12u + 4u + len;
        latest[id] = loc;
    }
    s.dataLoc = std::move(latest);
    s.dataFile = std::move(blob);
    diag_log(s, "load_data: records=%zu bytes=%zu", s.dataLoc.size(), s.dataFile.size());
    return KSBOX_OK;
}

#pragma endregion

#pragma region tomb

// 墓碑文件：追加 removedIds 记录（定长 16 字节：id(8)+deleted(1)+reserved(7)）
bool write_tomb(ksbx_store& s)
{
    if (s.removedIds.empty()) return true;

    std::vector<uint8_t> recs;
    for (long long rid : s.removedIds) {
        put_i64(recs, rid);
        recs.push_back(1); // deleted
        recs.insert(recs.end(), 7, 0);
    }

    bool exists = file_exists(s.tombPath);
    FILE* f = nullptr;
    if (exists) {
        if (_wfopen_s(&f, s.tombPath.c_str(), L"ab") != 0 || !f) return false;
    } else {
        if (_wfopen_s(&f, s.tombPath.c_str(), L"wb") != 0 || !f) return false;
        const uint8_t hdr[8] = { 'K','S','X','T', 1,0,0,0 };
        if (fwrite(hdr, 1, 8, f) != 8) { fclose(f); return false; }
        s.tombFile.assign(hdr, hdr + 8);
    }
    size_t w = fwrite(recs.data(), 1, recs.size(), f);
    fclose(f);
    if (w != recs.size()) return false;
    s.tombFile.insert(s.tombFile.end(), recs.begin(), recs.end());
    diag_log(s, "write_tomb: appended=%zu totalBytes=%zu", s.removedIds.size(), s.tombFile.size());
    return true;
}

// 墓碑是否已达上限（触发 data 压缩回收空间）
bool tomb_over_limit(const ksbx_store& s)
{
    if (s.tombMaxCount > 0 && (uint32_t)(s.tombFile.size() / 16) >= s.tombMaxCount) return true;
    if (s.tombMaxBytes > 0 && (uint32_t)s.tombFile.size() >= s.tombMaxBytes) return true;
    return false;
}

// 加载 tomb 文件，建立 removedIds（已删除 id）
bool load_tomb(ksbx_store& s)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, s.tombPath.c_str(), L"rb") != 0 || !f) {
        s.tombFile.clear(); // 无 tomb 文件合法（全新库）
        return true;
    }
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 8) { fclose(f); s.tombFile.clear(); return true; }
    std::vector<uint8_t> blob((size_t)sz);
    size_t r = fread(blob.data(), 1, sz, f);
    fclose(f);
    if (r != (size_t)sz) return false;
    if (blob[0] != 'K' || blob[1] != 'S' || blob[2] != 'X' || blob[3] != 'T') return false;
    size_t p = 8; // 跳过 magic+ver
    while (p + 16 <= blob.size()) {
        long long id = 0;
        for (int i = 0; i < 8; i++) id |= (long long)blob[p + i] << (8 * i);
        p += 16;
        s.removedIds.push_back(id);
    }
    s.tombFile = std::move(blob);
    diag_log(s, "load_tomb: records=%zu bytes=%zu", s.removedIds.size(), s.tombFile.size());
    return true;
}

// 压缩：重建 data 为仅含有效密文，并清空 tomb 文件
bool compact_data(ksbx_store& s)
{
    if (!rebuild_data(s)) return false;

    std::wstring tmp = s.tombPath + L".tmp";
    FILE* tf = nullptr;
    if (_wfopen_s(&tf, tmp.c_str(), L"wb") != 0 || !tf) return false;
    const uint8_t thdr[8] = { 'K','S','X','T', 1,0,0,0 };
    size_t wt = fwrite(thdr, 1, 8, tf);
    fclose(tf);
    if (wt != 8) return false;
    if (MoveFileExW(tmp.c_str(), s.tombPath.c_str(), MOVEFILE_REPLACE_EXISTING) == 0)
        return false;
    s.tombFile.assign(thdr, thdr + 8);
    s.removedIds.clear();
    diag_log(s, "compact_data: OK");
    return true;
}

#pragma endregion

#pragma region recovery

// 恢复密钥独立存储（<base>.recovery），机密性等同账号/密码。
// 记录布局与 data 相同：id(i64) nonce(12) len(u32) cipher+tag(len)。

std::vector<uint8_t> build_recovery_record(ksbx_store& s, long long id,
                                           const std::vector<std::wstring>& keys)
{
    std::vector<uint8_t> rec;
    std::vector<uint8_t> nonce, blob;
    if (!encrypt_blob(s, serialize_recovery(keys), nonce, blob)) return {};
    put_i64(rec, id);
    rec.insert(rec.end(), nonce.begin(), nonce.end());
    put_u32(rec, (uint32_t)blob.size());
    rec.insert(rec.end(), blob.begin(), blob.end());
    return rec;
}

// 解析 recovery 记录流，重建 id -> 定位映射（同 id 仅保留最后一条）
bool scan_recovery_records(const std::vector<uint8_t>& blob,
                           std::unordered_map<long long, DataLoc>& out)
{
    out.clear();
    if (blob.size() < 8) return false;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(blob, p, ver)) return false;
    if (ver != 1) return false;
    std::unordered_map<long long, DataLoc> latest;
    while (p < blob.size()) {
        long long id = 0;
        std::vector<uint8_t> nonce;
        uint32_t len = 0;
        if (!get_i64(blob, p, id)) break;
        if (!get_bytes(blob, p, 12, nonce)) break;
        if (!get_u32(blob, p, len)) break;
        uint64_t recStart = (uint64_t)(p - 8 - 12 - 4);
        if (!get_bytes(blob, p, len, nonce)) break;
        DataLoc loc;
        loc.offset = recStart;
        loc.total = 8u + 12u + 4u + len;
        latest[id] = loc;
    }
    out = std::move(latest);
    return true;
}

bool load_recovery(ksbx_store& s)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, s.recoveryPath.c_str(), L"rb") != 0 || !f) {
        s.recoveryFile.clear();
        s.recoveryLoc.clear();
        return true; // 无 recovery 文件合法（旧库/未使用本功能）
    }
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 8) { fclose(f); s.recoveryFile.clear(); s.recoveryLoc.clear(); return true; }
    std::vector<uint8_t> blob((size_t)sz);
    size_t r = fread(blob.data(), 1, sz, f);
    fclose(f);
    if (r != (size_t)sz) return false;
    if (blob[0] != 'K' || blob[1] != 'S' || blob[2] != 'X' || blob[3] != 'R') return false;
    s.recoveryFile = std::move(blob);
    bool ok = scan_recovery_records(s.recoveryFile, s.recoveryLoc);
    diag_log(s, "load_recovery: records=%zu", s.recoveryLoc.size());
    return ok;
}

// 重建 recovery 文件并原子替换：未改动记录原样拷贝，改动项重加密，已删除项消失。
bool write_recovery(ksbx_store& s)
{
    if (!s.recoveryDirty) return true;

    std::vector<uint8_t> out;
    const uint8_t hdr[8] = { 'K','S','X','R', 1,0,0,0 };
    out.insert(out.end(), hdr, hdr + 8);

    // 按文件偏移顺序处理历史记录，保证输出紧凑确定
    std::vector<std::pair<uint64_t, long long>> order;
    order.reserve(s.recoveryLoc.size());
    for (const auto& kv : s.recoveryLoc) order.push_back({ kv.second.offset, kv.first });
    std::sort(order.begin(), order.end());
    for (const auto& pr : order) {
        long long id = pr.second;
        auto cur = s.recoveryCache.find(id);
        if (cur != s.recoveryCache.end()) {
            std::vector<uint8_t> rec = build_recovery_record(s, id, cur->second);
            if (rec.empty()) return false;
            out.insert(out.end(), rec.begin(), rec.end());
        } else {
            const auto& loc = s.recoveryLoc[id];
            if (loc.offset + loc.total > s.recoveryFile.size()) return false;
            out.insert(out.end(), s.recoveryFile.begin() + loc.offset,
                       s.recoveryFile.begin() + loc.offset + loc.total);
        }
    }
    // 新增 id（不在历史中）
    for (const auto& kv : s.recoveryCache) {
        if (s.recoveryLoc.find(kv.first) != s.recoveryLoc.end()) continue;
        std::vector<uint8_t> rec = build_recovery_record(s, kv.first, kv.second);
        if (rec.empty()) return false;
        out.insert(out.end(), rec.begin(), rec.end());
    }

    std::wstring tmp = s.recoveryPath + L".tmp";
    FILE* f = nullptr;
    if (_wfopen_s(&f, tmp.c_str(), L"wb") != 0 || !f) return false;
    size_t w = fwrite(out.data(), 1, out.size(), f);
    fclose(f);
    if (w != out.size()) return false;
    if (MoveFileExW(tmp.c_str(), s.recoveryPath.c_str(), MOVEFILE_REPLACE_EXISTING) == 0)
        return false;

    s.recoveryFile = std::move(out);
    if (!scan_recovery_records(s.recoveryFile, s.recoveryLoc)) return false;
    s.recoveryCache.clear();
    s.recoveryDirty = false;
    diag_log(s, "write_recovery: records=%zu bytes=%zu", s.recoveryLoc.size(), s.recoveryFile.size());
    return true;
}

#pragma endregion
