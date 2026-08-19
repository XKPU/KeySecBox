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

#pragma region 通用工具

// 用 Win32 属性查询代替打开文件句柄。
bool file_exists(const std::wstring& path)
{
    DWORD attr = GetFileAttributesW(path.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
}

bool read_file_bytes(const std::wstring& path, std::vector<uint8_t>& out)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"rb") != 0 || !f) return false;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 0) { fclose(f); return false; }
    out.assign((size_t)sz, 0);
    size_t r = fread(out.data(), 1, (size_t)sz, f);
    fclose(f);
    return r == (size_t)sz;
}

// 先写临时文件再原子替换
bool atomic_write_file(const std::wstring& path, const std::vector<uint8_t>& data)
{
    std::wstring tmp = path + L".tmp";
    FILE* f = nullptr;
    if (_wfopen_s(&f, tmp.c_str(), L"wb") != 0 || !f) return false;
    size_t w = fwrite(data.data(), 1, data.size(), f);
    fclose(f);
    if (w != data.size()) return false;
    if (MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING) == 0)
        return false;
    return true;
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

// 派生密钥并初始化 GCM 句柄。事务性：先用临时 key + 临时 ctx 全部成功，
// 才提交到 s.key / s.gcm；派生或初始化失败不破坏当前会话（改密失败可回滚）。
bool derive_for_store(ksbx_store& s, const std::wstring& masterPwd)
{
    std::vector<uint8_t> tmpKey(32, 0);
    if (!ksbx::crypto::derive_key(masterPwd, s.salt, s.iterations, tmpKey)) return false;
    ksbx::crypto::GcmCtx tmpGcm;
    if (!tmpGcm.init(tmpKey)) {
        std::fill(tmpKey.begin(), tmpKey.end(), 0); // 抹除瞬态密钥
        return false;
    }
    std::fill(s.key.begin(), s.key.end(), 0);
    s.key = std::move(tmpKey);
    s.gcm = std::move(tmpGcm);
    return true;
}

// 校验密码。
bool verify_password(ksbx_store& s)
{
    std::string chk;
    if (!decrypt_blob(s, s.chkNonce, s.chkBlob, chk)) return false;
    bool ok = s.legacyMode ? (chk == "KSX3-OK") : (chk == MASTER_CHECK);
    std::fill(chk.begin(), chk.end(), '\0'); // 抹除瞬态明文
    return ok;
}

static std::wstring utf8_to_w(const std::string& u)
{
    if (u.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, u.c_str(), -1, nullptr, 0);
    if (n <= 1) return L"";
    std::wstring w(n - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, u.c_str(), -1, &w[0], n);
    return w;
}

#pragma endregion

#pragma region 1.1.x  读取

// <base>.prefs 可选（缺省 = 默认偏好）
bool load_prefs(ksbx_store& s)
{
    if (!file_exists(s.prefsPath)) { s.diag = false; return true; }
    std::vector<uint8_t> blob;
    if (!read_file_bytes(s.prefsPath, blob)) return false;
    std::string text(blob.begin(), blob.end());
    return deserialize_prefs_doc(s, text);
}

// <base>.master 必须：magic KSXM + ver + salt(16) + kdf(u8) + iterations(u32) + 校验块
// 最小长度：4+4+16+1+4+12+4+16(tag)=61
bool load_master(ksbx_store& s)
{
    std::vector<uint8_t> blob;
    if (!read_file_bytes(s.masterPath, blob)) return false;
    if (blob.size() < 61) return false;
    if (memcmp(blob.data(), MAGIC_MASTER, 4) != 0) return false;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(blob, p, ver)) return false;
    if (ver != 1) return false;
    if (p + 16 > blob.size()) return false;
    s.salt.assign(blob.begin() + p, blob.begin() + p + 16); p += 16;
    uint8_t kdf = 0;
    if (!get_u8(blob, p, kdf)) return false;
    if (kdf != KDF_PBKDF2) return false;
    if (!get_u32(blob, p, s.iterations)) return false;
    std::vector<uint8_t> cNonce, cBlob;
    if (!get_bytes(blob, p, 12, cNonce)) return false;
    uint32_t cLen = 0;
    if (!get_u32(blob, p, cLen)) return false;
    if (!get_bytes(blob, p, cLen, cBlob)) return false;
    s.chkNonce = std::move(cNonce);
    s.chkBlob = std::move(cBlob);
    diag_log(s, "load_master: OK iterations=%u", s.iterations);
    return true;
}

// <base>.cats 必须：明文分类数组（数组序 = 显示序）
bool load_cats(ksbx_store& s)
{
    std::vector<uint8_t> blob;
    if (!read_file_bytes(s.catsPath, blob)) return false;
    std::string text(blob.begin(), blob.end());
    if (!deserialize_cats_doc(s, text)) return false;
    return true;
}

// <base>.map 必须：计数器 + 条目↔分类关联 + 全部视图 pins
bool load_map(ksbx_store& s)
{
    std::vector<uint8_t> blob;
    if (!read_file_bytes(s.mapPath, blob)) return false;
    std::string text(blob.begin(), blob.end());
    return deserialize_map_doc(s, text);
}

// 扫描 entries 记录流（从 start 起）：填充 metas 明文 account/note + entriesLoc。
// 仅对已存在于 metas 的条目做 UTF8 转换，孤儿记录（防御数据）跳过转换。
static bool scan_entries(ksbx_store& s, const std::vector<uint8_t>& blob, size_t start)
{
    size_t p = start;
    while (p < blob.size()) {
        uint64_t recStart = (uint64_t)p;
        long long id = 0;
        if (!get_i64(blob, p, id)) break;
        uint32_t accLen = 0;
        if (!get_u32(blob, p, accLen)) break;
        if (p + accLen > blob.size()) break;
        size_t accStart = p; p += accLen;
        uint32_t noteLen = 0;
        if (!get_u32(blob, p, noteLen)) break;
        if (p + noteLen > blob.size()) break;
        size_t noteStart = p; p += noteLen;
        if (p + 12 > blob.size()) break; // nonce
        p += 12;
        uint32_t pwLen = 0;
        if (!get_u32(blob, p, pwLen)) break;
        if (p + pwLen > blob.size()) break;
        p += pwLen;
        DataLoc loc;
        loc.offset = recStart;
        loc.total = (uint32_t)(p - recStart);
        s.entriesLoc[id] = loc;
        auto mit = s.metas.find(id);
        if (mit != s.metas.end()) {
            mit->second.account = utf8_to_w(
                std::string(blob.begin() + accStart, blob.begin() + accStart + accLen));
            mit->second.note = utf8_to_w(
                std::string(blob.begin() + noteStart, blob.begin() + noteStart + noteLen));
        }
    }
    return true;
}

// <base>.entries 必须：magic KSXE + ver + 记录流
bool load_entries(ksbx_store& s)
{
    if (!read_file_bytes(s.entriesPath, s.entriesFile)) return false;
    if (s.entriesFile.size() < 8) return false;
    if (memcmp(s.entriesFile.data(), MAGIC_ENTRIES, 4) != 0) return false;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(s.entriesFile, p, ver)) return false;
    if (ver != 1) return false;
    s.entriesLoc.clear();
    scan_entries(s, s.entriesFile, p);
    diag_log(s, "load_entries: records=%zu bytes=%zu", s.entriesLoc.size(), s.entriesFile.size());
    return true;
}

// 扫描 recovery 记录流：id(i64) nonce(12) len(u32) cipher+tag（同 id 仅保留最后一条）
static bool scan_recovery(const std::vector<uint8_t>& blob,
                          std::unordered_map<long long, DataLoc>& out)
{
    out.clear();
    if (blob.size() < 8) return false;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(blob, p, ver)) return false;
    if (ver != 1) return false;
    while (p < blob.size()) {
        uint64_t recStart = (uint64_t)p;
        long long id = 0;
        std::vector<uint8_t> nonce;
        uint32_t len = 0;
        if (!get_i64(blob, p, id)) break;
        if (!get_bytes(blob, p, 12, nonce)) break;
        if (!get_u32(blob, p, len)) break;
        if (p + len > blob.size()) break;
        p += len;
        DataLoc loc;
        loc.offset = recStart;
        loc.total = (uint32_t)(p - recStart);
        out[id] = loc;
    }
    return true;
}

// <base>.recovery 可选：magic KSXR + 记录流
bool load_recovery(ksbx_store& s)
{
    if (!file_exists(s.recoveryPath)) {
        s.recoveryFile.clear();
        s.recoveryLoc.clear();
        return true; // 无 recovery 文件合法
    }
    if (!read_file_bytes(s.recoveryPath, s.recoveryFile)) return false;
    if (s.recoveryFile.size() < 8) return false;
    if (memcmp(s.recoveryFile.data(), MAGIC_RECOVERY, 4) != 0) return false;
    if (!scan_recovery(s.recoveryFile, s.recoveryLoc)) return false;
    diag_log(s, "load_recovery: records=%zu", s.recoveryLoc.size());
    return true;
}

#pragma endregion

#pragma region 1.1.x 写入

bool write_prefs(ksbx_store& s)
{
    std::string text = serialize_prefs_doc(s);
    std::vector<uint8_t> data(text.begin(), text.end());
    if (!atomic_write_file(s.prefsPath, data)) return false;
    diag_log(s, "write_prefs: OK diag=%d", s.diag ? 1 : 0);
    return true;
}

// setup / change_password 时写：盐+KDF+校验块
bool write_master(ksbx_store& s)
{
    std::vector<uint8_t> blob;
    blob.insert(blob.end(), MAGIC_MASTER, MAGIC_MASTER + 4);
    put_u32(blob, 1); // 格式版本 1
    for (uint8_t x : s.salt) blob.push_back(x);
    blob.push_back(KDF_PBKDF2);
    put_u32(blob, s.iterations);

    std::vector<uint8_t> cNonce, cBlob;
    if (!encrypt_blob(s, MASTER_CHECK, cNonce, cBlob)) return false;
    for (uint8_t x : cNonce) blob.push_back(x);
    put_u32(blob, (uint32_t)cBlob.size());
    for (uint8_t x : cBlob) blob.push_back(x);

    if (!atomic_write_file(s.masterPath, blob)) return false;
    diag_log(s, "write_master: bytes=%zu", blob.size());
    return true;
}

bool write_cats(ksbx_store& s)
{
    std::string text = serialize_cats_doc(s);
    std::vector<uint8_t> data(text.begin(), text.end());
    if (!atomic_write_file(s.catsPath, data)) return false;
    diag_log(s, "write_cats: OK cats=%zu", s.categories.size());
    return true;
}

bool write_map(ksbx_store& s)
{
    std::string text = serialize_map_doc(s);
    std::vector<uint8_t> data(text.begin(), text.end());
    if (!atomic_write_file(s.mapPath, data)) return false;
    diag_log(s, "write_map: OK entries=%zu", s.metas.size());
    return true;
}

// 全量重写 entries：仅密码加密，账户/备注明文。
// 未修改条目直接从旧文件拷贝原始记录（不再解密），避免不必要开销。
bool write_entries(ksbx_store& s)
{
    if (s.secretCache.empty()) return true; // 无条目内容变更

    std::vector<uint8_t> out;
    out.insert(out.end(), MAGIC_ENTRIES, MAGIC_ENTRIES + 4);
    put_u32(out, 1);

    std::vector<long long> ids;
    for (const auto& kv : s.metas) ids.push_back(kv.first);
    std::sort(ids.begin(), ids.end());

    // 构建输出时同步重建 entriesLoc（免去写后二次全文件扫描）
    std::unordered_map<long long, DataLoc> newLocs;
    newLocs.reserve(ids.size());
    for (long long id : ids) {
        uint64_t recStart = (uint64_t)out.size();
        auto c = s.secretCache.find(id);
        if (c != s.secretCache.end()) {
            std::vector<uint8_t> rec = build_entry_record(s, id, c->second);
            if (rec.empty()) return false;
            out.insert(out.end(), rec.begin(), rec.end());
        } else {
            auto locIt = s.entriesLoc.find(id);
            if (locIt == s.entriesLoc.end() || s.entriesFile.empty()) return false;
            const auto& loc = locIt->second;
            if (loc.offset + loc.total > s.entriesFile.size()) return false;
            out.insert(out.end(), s.entriesFile.begin() + loc.offset,
                       s.entriesFile.begin() + loc.offset + loc.total);
        }
        DataLoc nl;
        nl.offset = recStart;
        nl.total = (uint32_t)(out.size() - recStart);
        newLocs[id] = nl;
    }
    if (!atomic_write_file(s.entriesPath, out)) return false;
    s.entriesFile = std::move(out);
    s.entriesLoc = std::move(newLocs);
    s.secretCache.clear();
    diag_log(s, "write_entries: records=%zu bytes=%zu", s.entriesLoc.size(), s.entriesFile.size());
    return true;
}

// 重建 recovery 文件并原子替换：未改动记录原样拷贝，改动项重加密，已删除项消失
bool write_recovery(ksbx_store& s)
{
    if (!s.recoveryDirty) return true;

    std::vector<uint8_t> out;
    out.insert(out.end(), MAGIC_RECOVERY, MAGIC_RECOVERY + 4);
    put_u32(out, 1);

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

    if (!atomic_write_file(s.recoveryPath, out)) return false;
    s.recoveryFile = std::move(out);
    if (!scan_recovery(s.recoveryFile, s.recoveryLoc)) return false;
    s.recoveryCache.clear();
    s.recoveryDirty = false;
    diag_log(s, "write_recovery: records=%zu bytes=%zu", s.recoveryLoc.size(), s.recoveryFile.size());
    return true;
}

#pragma endregion

#pragma region 旧版 1.0.x 读取

// 旧版 <base>.settings：magic KSX3 + ver(1/2) + salt(16) + [kdf(u8)] + iterations(u32)
//   + chkNonce(12) + chkLen(u32) + chkBlob [+ 扩展 JSON（tomb/diag，忽略）]
int load_settings_legacy(ksbx_store& s, const std::wstring& path)
{
    std::vector<uint8_t> blob;
    if (!read_file_bytes(path, blob)) return KSBOX_ERR_NO_VAULT;
    if (blob.size() < 40) return KSBOX_ERR_IO;
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
        if (kdf != KDF_PBKDF2) return KSBOX_ERR_IO;
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
    return KSBOX_OK;
}

// 旧版 index JSON 解析。支持单分类 catId 与多分类 cats。
static bool deserialize_index_legacy(ksbx_store& s, const std::string& text)
{
    bool ok = false;
    Value root = parse(text, ok);
    if (!ok || root.type != Value::Obj) return false;

    s.categories.clear();
    s.catIndex.clear();
    s.metas.clear();
    s.catOrder.clear();

    s.nextCatId = get_i64(root, "nextCatId");
    s.nextEntryId = get_i64(root, "nextEntryId");

    auto cit = root.obj.find("cats");
    if (cit != root.obj.end() && cit->second.type == Value::Arr) {
        for (const auto& c : cit->second.arr) {
            Category cat;
            cat.id = get_i64(c, "id");
            cat.name = get_str(c, "name");
            s.categories[cat.id] = cat;
            s.catIndex[cat.id];
            if (cat.id >= s.nextCatId) s.nextCatId = cat.id + 1;
            if (std::find(s.catOrder.begin(), s.catOrder.end(), cat.id) == s.catOrder.end())
                s.catOrder.push_back(cat.id);
        }
    }
    if (s.categories.find(UNCAT_ID) == s.categories.end()) {
        Category uc; uc.id = UNCAT_ID; uc.name = UNCAT_NAME;
        s.categories[UNCAT_ID] = uc;
        s.catIndex[UNCAT_ID];
        s.catOrder.insert(s.catOrder.begin(), UNCAT_ID);
    } else {
        auto uIt = std::find(s.catOrder.begin(), s.catOrder.end(), UNCAT_ID);
        if (uIt != s.catOrder.end()) {
            s.catOrder.erase(uIt);
            s.catOrder.insert(s.catOrder.begin(), UNCAT_ID);
        }
    }

    auto iit = root.obj.find("items");
    if (iit != root.obj.end() && iit->second.type == Value::Arr) {
        for (const auto& e : iit->second.arr) {
            EntryMeta m;
            m.id = get_i64(e, "id");
            auto cit2 = e.obj.find("cats");
            if (cit2 != e.obj.end() && cit2->second.type == Value::Arr) {
                for (const auto& c : cit2->second.arr)
                    if (c.type == Value::Num) m.catIds.push_back((long long)c.num);
            } else {
                m.catIds.push_back(get_i64(e, "catId"));
            }
            m.note = get_str(e, "note");
            std::vector<long long> valid;
            for (long long cid : m.catIds)
                if (s.categories.find(cid) != s.categories.end())
                    valid.push_back(cid);
            m.catIds = valid.empty() ? std::vector<long long>{ UNCAT_ID } : std::move(valid);
            s.metas[m.id] = m;
            for (long long cid : m.catIds)
                s.catIndex[cid].push_back(m.id);
            if (m.id >= s.nextEntryId) s.nextEntryId = m.id + 1;
        }
    }
    return true;
}

// 旧版 <base>.index：magic KSXI + ver(1=明文,2=明文,3=整块加密)
int load_index_legacy(ksbx_store& s, const std::wstring& path)
{
    std::vector<uint8_t> blob;
    if (!read_file_bytes(path, blob)) return KSBOX_ERR_IO;
    if (blob.size() < 8) return KSBOX_ERR_IO;
    if (blob[0] != 'K' || blob[1] != 'S' || blob[2] != 'X' || blob[3] != 'I')
        return KSBOX_ERR_IO;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(blob, p, ver)) return KSBOX_ERR_IO;

    std::string text;
    if (ver == 1 || ver == 2) {
        text.assign(blob.begin() + p, blob.end());
    } else if (ver == 3) {
        std::vector<uint8_t> nonce;
        if (!get_bytes(blob, p, 12, nonce)) return KSBOX_ERR_IO;
        uint32_t len = 0;
        if (!get_u32(blob, p, len)) return KSBOX_ERR_IO;
        if (p + len > blob.size()) return KSBOX_ERR_IO;
        std::vector<uint8_t> cipher(blob.begin() + p, blob.begin() + p + len);
        if (!decrypt_blob(s, nonce, cipher, text)) return KSBOX_ERR_IO;
    } else {
        return KSBOX_ERR_IO;
    }
    if (!deserialize_index_legacy(s, text)) return KSBOX_ERR_IO;
    std::fill(text.begin(), text.end(), '\0');
    diag_log(s, "load_index_legacy: OK ver=%u cats=%zu items=%zu", ver, s.categories.size(), s.metas.size());
    return KSBOX_OK;
}

// 旧版 <base>.data：magic KSXD + ver=2 + 记录流 id(i64) nonce(12) len(u32) cipher+tag
int load_data_legacy(ksbx_store& s, const std::wstring& path)
{
    if (!read_file_bytes(path, s.entriesFile)) return KSBOX_ERR_IO;
    if (s.entriesFile.size() < 8) return KSBOX_ERR_IO;
    if (s.entriesFile[0] != 'K' || s.entriesFile[1] != 'S' || s.entriesFile[2] != 'X' || s.entriesFile[3] != 'D')
        return KSBOX_ERR_IO;
    size_t p = 4;
    uint32_t ver = 0;
    if (!get_u32(s.entriesFile, p, ver)) return KSBOX_ERR_IO;
    if (ver != 2) return KSBOX_ERR_IO;

    s.entriesLoc.clear();
    while (p < s.entriesFile.size()) {
        uint64_t recStart = (uint64_t)p;
        long long id = 0;
        std::vector<uint8_t> nonce;
        uint32_t len = 0;
        if (!get_i64(s.entriesFile, p, id)) break;
        if (!get_bytes(s.entriesFile, p, 12, nonce)) break;
        if (!get_u32(s.entriesFile, p, len)) break;
        if (p + len > s.entriesFile.size()) break;
        p += len;
        DataLoc loc;
        loc.offset = recStart;
        loc.total = (uint32_t)(p - recStart);
        s.entriesLoc[id] = loc; // 同 id 仅保留最后一条
    }
    diag_log(s, "load_data_legacy: records=%zu bytes=%zu", s.entriesLoc.size(), s.entriesFile.size());
    return KSBOX_OK;
}

// 旧版 <base>.recovery：与 新版布局一致（magic KSXR + 记录流）
bool load_recovery_legacy(ksbx_store& s, const std::wstring& path)
{
    if (!file_exists(path)) { s.recoveryFile.clear(); s.recoveryLoc.clear(); return true; }
    if (!read_file_bytes(path, s.recoveryFile)) return false;
    if (s.recoveryFile.size() < 8) return false;
    if (memcmp(s.recoveryFile.data(), MAGIC_RECOVERY, 4) != 0) return false;
    if (!scan_recovery(s.recoveryFile, s.recoveryLoc)) return false;
    diag_log(s, "load_recovery_legacy: records=%zu", s.recoveryLoc.size());
    return true;
}

#pragma endregion
