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

// 多文件格式 (KSX3)，均位于同一目录，basename 由 file 参数推导：
//   <base>.settings   盐+KDF参数 + 校验块 + 扩展设置(JSON)
//   <base>.index      分类+条目 meta（明文 JSON）
//   <base>.data       AES-GCM 逐条独立加密（追加写+墓碑）
//   <base>.tomb       墓碑（定长记录，已删除/失效 id）
//   <base>.recovery   恢复密钥（AES-GCM 逐条独立加密）
//
// 密钥派生：解密密钥 = KDF(密码, 盐)。盐等"加密证书"存于 settings。
// 机密内存策略：账号/密码仅查询/编辑时瞬时解密，不常驻。
// 内置分类"未分类" id=0，setup 自动建立，不可删除/重命名。

#pragma region 内部工具

void to_lower(std::wstring& s)
{
    std::transform(s.begin(), s.end(), s.begin(), ::towlower);
}

std::wstring path_with_ext(const std::wstring& base, const wchar_t* ext)
{
    return base + ext;
}

void index_meta(ksbx_store& s, const EntryMeta& m)
{
    s.metas[m.id] = m;
    s.catIndex[m.categoryId].push_back(m.id);
    if (m.id >= s.nextEntryId) s.nextEntryId = m.id + 1;
}

// 按需解密某条目恢复密钥。失败返回 false，成功填充 keys。
bool decrypt_recovery_keys(ksbx_store& s, long long id, std::vector<std::wstring>& keys)
{
    auto locIt = s.recoveryLoc.find(id);
    if (locIt == s.recoveryLoc.end() || s.recoveryFile.empty()) return false;
    const auto& loc = locIt->second;
    if (loc.offset + loc.total > s.recoveryFile.size()) return false;
    const uint8_t* base = s.recoveryFile.data() + loc.offset;
    std::vector<uint8_t> nonce(base + 8, base + 20);
    uint32_t len = (uint32_t)base[20] | ((uint32_t)base[21] << 8) |
                   ((uint32_t)base[22] << 16) | ((uint32_t)base[23] << 24);
    if (loc.total < 24 + len) return false;
    std::vector<uint8_t> cipher(base + 24, base + 24 + len);
    std::string plain;
    if (!decrypt_blob(s, nonce, cipher, plain)) return false;
    bool ok = false;
    Value v = parse(plain, ok);
    std::fill(plain.begin(), plain.end(), '\0'); // 抹除瞬态明文
    if (!ok || v.type != Value::Arr) return false;
    keys.clear();
    for (const auto& e : v.arr)
        if (e.type == Value::Str) keys.push_back(unescape(e.str));
    return true;
}

wchar_t* to_wcs(const std::string& utf8)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, nullptr, 0);
    if (n <= 0) return nullptr;
    wchar_t* out = static_cast<wchar_t*>(std::malloc(n * sizeof(wchar_t)));
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, out, n);
    return out;
}

// 按需解密某条目 secret（账号+密码+备注）。明文用完即弃，不驻留。
bool peek_secret(ksbx_store& s, long long id, std::wstring& account, std::wstring& password, std::wstring& note)
{
    auto cacheIt = s.secretCache.find(id);
    if (cacheIt != s.secretCache.end()) {
        account = cacheIt->second.account;
        password = cacheIt->second.password;
        note = cacheIt->second.note;
        return true;
    }
    auto locIt = s.dataLoc.find(id);
    if (locIt == s.dataLoc.end() || s.dataFile.empty()) return false;
    const auto& loc = locIt->second;
    if (loc.offset + loc.total > s.dataFile.size()) return false;
    const uint8_t* base = s.dataFile.data() + loc.offset;
    // 记录布局: id(8) nonce(12) len(4) cipher+tag(len，len 已含 16 字节 tag)
    std::vector<uint8_t> nonce(base + 8, base + 20);
    uint32_t len = (uint32_t)base[20] | ((uint32_t)base[21] << 8) |
                   ((uint32_t)base[22] << 16) | ((uint32_t)base[23] << 24);
    if (loc.total < 24 + len) return false;
    std::vector<uint8_t> cipher(base + 24, base + 24 + len);
    std::string plain;
    if (!decrypt_blob(s, nonce, cipher, plain)) return false;
    deserialize_secret(plain, account, password, note);
    std::fill(plain.begin(), plain.end(), '\0'); // 抹除瞬态明文
    return true;
}

// 列表/搜索：按需解密每条目的账号/备注（瞬时明文不常驻）。
// 密码不在此解密，仅在 ksbx_get_entry 中单独解密。
std::string entries_to_json(ksbx_store* s, const std::vector<long long>& ids)
{
    std::string out = "[";
    bool first = true;
    for (long long id : ids) {
        auto it = s->metas.find(id);
        if (it == s->metas.end()) continue;
        const auto& m = it->second;
        std::wstring account, password, note;
        if (!peek_secret(*s, id, account, password, note)) continue;
        if (note.empty()) note = m.note; // 旧库兼容：index 明文备注
        if (!first) out += ",";
        first = false;
        char buf[96];
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"categoryId\":%lld,\"account\":", m.id, m.categoryId);
        out += buf;
        out += escape(account);
        out += ",\"note\":"; out += escape(note);
        out += "}";
    }
    out += "]";
    return out;
}

#pragma endregion

extern "C" {

#pragma region 生命周期

KSBOX_API ksbx_store* ksbx_store_create()
{
    return new (std::nothrow) ksbx_store();
}

KSBOX_API void ksbx_store_destroy(ksbx_store* s)
{
    if (s) diag_log(*s, "store_destroy");
    delete s; // GcmCtx 析构自动释放 BCrypt 句柄
}

#pragma endregion

#pragma region 初始化 / 解锁

KSBOX_API int ksbx_open(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd)
{
    if (!s || !file || !masterPwd) return KSBOX_ERR_GENERIC;
    s->basePath = file;
    s->settingsPath = path_with_ext(file, L".settings");
    s->indexPath = path_with_ext(file, L".index");
    s->dataPath = path_with_ext(file, L".data");
    s->tombPath = path_with_ext(file, L".tomb");
    s->recoveryPath = path_with_ext(file, L".recovery");

    // 先解析盐与 KDF 参数，再按密码派生密钥，最后校验密码
    int rc = load_settings(*s);
    if (rc != KSBOX_OK) return rc;
    diag_log(*s, "open: settings loaded diag=%d", s->diag ? 1 : 0);
    if (!derive_for_store(*s, masterPwd)) return KSBOX_ERR_IO;
    if (!verify_password(*s)) {
        diag_log(*s, "open: WRONG_PASSWORD");
        return KSBOX_ERR_WRONG_PASSWORD;
    }
    rc = load_index(*s);
    if (rc != KSBOX_OK) { diag_log(*s, "open: load_index rc=%d", rc); return rc; }
    rc = load_data(*s);
    if (rc != KSBOX_OK) { diag_log(*s, "open: load_data rc=%d", rc); return rc; }
    if (!load_tomb(*s)) return KSBOX_ERR_IO;
    if (!load_recovery(*s)) return KSBOX_ERR_IO;
    s->indexDirty = false;
    s->unlocked = true;
    diag_log(*s, "open: OK cats=%zu entries=%zu dataLoc=%zu tombRecs=%zu recLoc=%zu",
             s->categories.size(), s->metas.size(), s->dataLoc.size(),
             s->removedIds.size(), s->recoveryLoc.size());
    return KSBOX_OK;
}

KSBOX_API int ksbx_setup(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd)
{
    if (!s || !file || !masterPwd || masterPwd[0] == L'\0') return KSBOX_ERR_GENERIC;
    s->basePath = file;
    s->settingsPath = path_with_ext(file, L".settings");
    s->indexPath = path_with_ext(file, L".index");
    s->dataPath = path_with_ext(file, L".data");
    s->tombPath = path_with_ext(file, L".tomb");
    s->recoveryPath = path_with_ext(file, L".recovery");

    ksbx::crypto::random_bytes(s->salt, 16);
    s->iterations = PBKDF2_ITERATIONS;
    s->tombMaxBytes = TOMB_DEFAULT_MAX_BYTES;
    s->tombMaxCount = TOMB_DEFAULT_MAX_COUNT;
    s->categories.clear();
    s->catIndex.clear();
    s->metas.clear();
    s->secretCache.clear();
    s->dataLoc.clear();
    s->dataFile.clear();
    s->tombFile.clear();
    s->removedIds.clear();
    s->recoveryLoc.clear();
    s->recoveryCache.clear();
    s->recoveryDirty = false;
    s->recoveryFile.clear();
    s->nextCatId = 1; s->nextEntryId = 1;
    s->indexDirty = false;

    // 内置"未分类"
    Category uc; uc.id = UNCAT_ID; uc.name = UNCAT_NAME;
    s->categories[UNCAT_ID] = uc;
    s->catIndex[UNCAT_ID];

    if (!derive_for_store(*s, masterPwd)) return KSBOX_ERR_IO;
    diag_log(*s, "setup: derived key, writing initial files");

    s->unlocked = true;
    if (!write_settings(*s)) return KSBOX_ERR_IO;
    if (!write_index(*s)) return KSBOX_ERR_IO;
    if (!rebuild_data(*s)) return KSBOX_ERR_IO;   // 建立空的 data 文件
    if (!write_tomb(*s)) return KSBOX_ERR_IO;
    diag_log(*s, "setup: OK");
    return KSBOX_OK;
}

// 校验旧密码：用参数密码派生临时密钥与 live 会话密钥比对，不改动会话。
KSBOX_API int ksbx_verify_password(ksbx_store* s, const wchar_t* masterPwd)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (!masterPwd || masterPwd[0] == L'\0') return KSBOX_ERR_GENERIC;
    std::vector<uint8_t> tmpKey(32, 0);
    if (!ksbx::crypto::derive_key(masterPwd, s->salt, s->iterations, tmpKey)) return KSBOX_ERR_IO;
    bool ok = (tmpKey == s->key);
    std::fill(tmpKey.begin(), tmpKey.end(), 0);
    return ok ? KSBOX_OK : KSBOX_ERR_WRONG_PASSWORD;
}

KSBOX_API int ksbx_change_password(ksbx_store* s, const wchar_t* newMasterPwd)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (!newMasterPwd || newMasterPwd[0] == L'\0') return KSBOX_ERR_GENERIC;
    // 先用旧 key 解密全部 secret 与恢复密钥（低频操作，短暂驻留）。
    // 任一失败即中止，避免重加密后丢数据。
    std::unordered_map<long long, SecretCache> all;
    for (const auto& kv : s->metas) {
        std::wstring account, password, note;
        if (!peek_secret(*s, kv.first, account, password, note)) return KSBOX_ERR_IO;
        all[kv.first] = SecretCache{ account, password, note };
    }
    for (const auto& kv : s->recoveryLoc) {
        std::vector<std::wstring> keys;
        if (!decrypt_recovery_keys(*s, kv.first, keys)) return KSBOX_ERR_IO;
        s->recoveryCache[kv.first] = std::move(keys);
    }
    s->recoveryDirty = true;
    ksbx::crypto::random_bytes(s->salt, 16);
    if (!derive_for_store(*s, newMasterPwd)) return KSBOX_ERR_IO;
    s->secretCache = std::move(all); // 全部条目需重加密
    s->indexDirty = true;
    // 先重加密数据体，最后写 settings（新盐+新校验块）。
    // 若中途失败，旧 settings 仍可让用户用旧密码打开。
    if (!write_index(*s)) return KSBOX_ERR_IO;
    if (!write_recovery(*s)) return KSBOX_ERR_IO;
    if (!rebuild_data(*s)) return KSBOX_ERR_IO; // 全部条目重加密，从头重建
    if (!write_tomb(*s)) return KSBOX_ERR_IO;
    if (!write_settings(*s)) return KSBOX_ERR_IO;
    s->secretCache.clear();
    s->removedIds.clear();
    diag_log(*s, "change_password: OK entries=%zu", s->metas.size());
    return KSBOX_OK;
}

#pragma endregion

#pragma region 分类

KSBOX_API long long ksbx_add_category(ksbx_store* s, const wchar_t* name)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (!name) return KSBOX_ERR_GENERIC;
    std::wstring nm = name;
    if (nm == UNCAT_NAME) return KSBOX_ERR_DUP; // 不允许重复创建"未分类"
    for (const auto& kv : s->categories)
        if (kv.second.name == nm) return KSBOX_ERR_DUP;
    Category c; c.id = s->nextCatId++; c.name = nm;
    s->categories[c.id] = c;
    s->catIndex[c.id];
    s->indexDirty = true;
    diag_log(*s, "add_category: OK id=%lld", c.id);
    return c.id;
}

KSBOX_API int ksbx_rename_category(ksbx_store* s, long long id, const wchar_t* name)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (id == UNCAT_ID) return KSBOX_ERR_GENERIC; // 内置分类不可改名
    auto it = s->categories.find(id);
    if (it == s->categories.end()) return KSBOX_ERR_NOT_FOUND;
    it->second.name = name ? name : L"";
    s->indexDirty = true;
    diag_log(*s, "rename_category: OK id=%lld", id);
    return KSBOX_OK;
}

KSBOX_API int ksbx_remove_category(ksbx_store* s, long long id)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (id == UNCAT_ID) return KSBOX_ERR_GENERIC; // 内置分类不可删
    auto it = s->categories.find(id);
    if (it == s->categories.end()) return KSBOX_ERR_NOT_FOUND;
    auto idx = s->catIndex.find(id);
    if (idx != s->catIndex.end()) {
        for (long long eid : idx->second) {
            s->metas.erase(eid);
            s->secretCache.erase(eid);
            s->dataLoc.erase(eid);
            s->removedIds.push_back(eid); // data 写墓碑
            if (s->recoveryLoc.erase(eid) > 0 || s->recoveryCache.erase(eid) > 0)
                s->recoveryDirty = true;
        }
        s->catIndex.erase(idx);
    }
    s->categories.erase(it);
    s->indexDirty = true;
    diag_log(*s, "remove_category: OK id=%lld entries=%zu", id, idx != s->catIndex.end() ? idx->second.size() : 0);
    return KSBOX_OK;
}

KSBOX_API wchar_t* ksbx_list_categories(ksbx_store* s)
{
    if (!s || !s->unlocked) return nullptr;
    std::string out = "[";
    bool first = true;
    // 按 id 排序，保证稳定顺序
    std::vector<long long> ids;
    ids.reserve(s->categories.size());
    for (const auto& kv : s->categories) ids.push_back(kv.first);
    std::sort(ids.begin(), ids.end());
    for (long long cid : ids) {
        const auto& c = s->categories.find(cid)->second;
        if (!first) out += ",";
        first = false;
        char buf[64];
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"name\":", c.id);
        out += buf;
        out += escape(c.name);
        out += "}";
    }
    out += "]";
    diag_log(*s, "list_categories: count=%zu", s->categories.size());
    return to_wcs(out);
}

#pragma endregion

#pragma region 条目

KSBOX_API long long ksbx_add_entry(ksbx_store* s, long long categoryId,
    const wchar_t* account, const wchar_t* password, const wchar_t* note)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (s->categories.find(categoryId) == s->categories.end()) return KSBOX_ERR_NOT_FOUND;
    EntryMeta m;
    m.id = s->nextEntryId++;
    m.categoryId = categoryId;
    m.note = note ? note : L"";
    m.hasNote = !m.note.empty();
    index_meta(*s, m);
    s->secretCache[m.id] = SecretCache{ account ? account : L"", password ? password : L"", note ? note : L"" };
    s->indexDirty = true;
    diag_log(*s, "add_entry: OK id=%lld catId=%lld", m.id, m.categoryId);
    return m.id;
}

KSBOX_API int ksbx_update_entry(ksbx_store* s, long long id,
    long long categoryId, const wchar_t* account, const wchar_t* password, const wchar_t* note)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    auto it = s->metas.find(id);
    if (it == s->metas.end()) return KSBOX_ERR_NOT_FOUND;
    if (s->categories.find(categoryId) == s->categories.end()) return KSBOX_ERR_NOT_FOUND;
    if (it->second.categoryId != categoryId) {
        auto& old = s->catIndex[it->second.categoryId];
        old.erase(std::remove(old.begin(), old.end(), id), old.end());
        s->catIndex[categoryId].push_back(id);
    }
    it->second.categoryId = categoryId;
    it->second.note = note ? note : L"";
    it->second.hasNote = !it->second.note.empty();
    s->secretCache[id] = SecretCache{ account ? account : L"", password ? password : L"", note ? note : L"" };
    s->indexDirty = true;
    diag_log(*s, "update_entry: OK id=%lld catId=%lld", id, categoryId);
    return KSBOX_OK;
}

KSBOX_API int ksbx_remove_entry(ksbx_store* s, long long id)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    auto it = s->metas.find(id);
    if (it == s->metas.end()) return KSBOX_ERR_NOT_FOUND;
    auto& idx = s->catIndex[it->second.categoryId];
    idx.erase(std::remove(idx.begin(), idx.end(), id), idx.end());
    s->metas.erase(it);
    s->secretCache.erase(id);
    s->dataLoc.erase(id);
    s->removedIds.push_back(id); // data 写墓碑（增量）
    // 同步清除/计划清除其恢复密钥记录
    if (s->recoveryLoc.erase(id) > 0 || s->recoveryCache.erase(id) > 0)
        s->recoveryDirty = true;
    s->indexDirty = true;
    diag_log(*s, "remove_entry: OK id=%lld", id);
    return KSBOX_OK;
}

KSBOX_API wchar_t* ksbx_get_entry(ksbx_store* s, long long id)
{
    if (!s || !s->unlocked) return nullptr;
    auto it = s->metas.find(id);
    if (it == s->metas.end()) return nullptr;
    const auto& m = it->second;
    // 唯一解密入口：瞬时解密该条账号+密码+备注，返回 JSON 后即弃
    std::wstring account, password, note;
    if (!peek_secret(*s, id, account, password, note)) return nullptr;
    if (note.empty()) note = m.note; // 旧库兼容：index 明文备注

    char buf[96];
    snprintf(buf, sizeof(buf), "{\"id\":%lld,\"categoryId\":%lld,\"account\":", m.id, m.categoryId);
    std::string out = buf;
    out += escape(account);
    out += ",\"password\":"; out += escape(password);
    out += ",\"note\":"; out += escape(note);
    out += "}";
    diag_log(*s, "get_entry: OK id=%lld", id);
    return to_wcs(out);
}

#pragma endregion

#pragma region 恢复密钥

KSBOX_API int ksbx_set_recovery(ksbx_store* s, long long id, const wchar_t* keysJson)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (s->metas.find(id) == s->metas.end()) return KSBOX_ERR_NOT_FOUND;
    std::vector<std::wstring> keys;
    parse_recovery_input(keysJson, keys);
    if (keys.empty()) {
        // 空数组 = 删除该条恢复记录
        if (s->recoveryLoc.erase(id) > 0 || s->recoveryCache.erase(id) > 0)
            s->recoveryDirty = true;
        return KSBOX_OK;
    }
    s->recoveryCache[id] = std::move(keys);
    s->recoveryDirty = true;
    diag_log(*s, "set_recovery: OK id=%lld keys=%zu", id, s->recoveryCache[id].size());
    return KSBOX_OK;
}

KSBOX_API wchar_t* ksbx_get_recovery(ksbx_store* s, long long id)
{
    if (!s || !s->unlocked) return nullptr;
    if (s->metas.find(id) == s->metas.end()) return nullptr;
    auto cacheIt = s->recoveryCache.find(id);
    if (cacheIt != s->recoveryCache.end())
        return to_wcs(serialize_recovery(cacheIt->second));
    std::vector<std::wstring> keys;
    if (!decrypt_recovery_keys(*s, id, keys)) return nullptr;
    diag_log(*s, "get_recovery: OK id=%lld keys=%zu", id, keys.size());
    return to_wcs(serialize_recovery(keys));
}

#pragma endregion

#pragma region 查询

KSBOX_API wchar_t* ksbx_query_all(ksbx_store* s)
{
    if (!s || !s->unlocked) return nullptr;
    std::vector<long long> ids;
    ids.reserve(s->metas.size());
    for (const auto& kv : s->metas) ids.push_back(kv.first);
    // 排序保证 UI 列表顺序可预期（按 id 升序）
    std::sort(ids.begin(), ids.end());
    std::string json = entries_to_json(s, ids);
    diag_log(*s, "query_all: count=%zu", s->metas.size());
    return to_wcs(json);
}

KSBOX_API wchar_t* ksbx_query_category(ksbx_store* s, long long categoryId)
{
    if (!s || !s->unlocked) return nullptr;
    auto idx = s->catIndex.find(categoryId);
    std::vector<long long> ids = (idx != s->catIndex.end()) ? idx->second : std::vector<long long>{};
    std::string json = entries_to_json(s, ids);
    diag_log(*s, "query_category: OK catId=%lld count=%zu", categoryId, ids.size());
    return to_wcs(json);
}

KSBOX_API wchar_t* ksbx_search(ksbx_store* s, const wchar_t* keyword)
{
    if (!s || !s->unlocked) return nullptr;
    std::wstring kw = keyword ? keyword : L"";
    to_lower(kw);
    std::vector<long long> ids;
    for (const auto& kv : s->metas) {
        const auto& m = kv.second;
        // 账号与备注存于加密 secret，需瞬时解密比对
        std::wstring account, password, note;
        if (!peek_secret(*s, kv.first, account, password, note)) continue;
        std::wstring a = account;
        std::wstring n = note.empty() ? m.note : note;
        to_lower(a);
        to_lower(n);
        if (a.find(kw) != std::wstring::npos || n.find(kw) != std::wstring::npos)
            ids.push_back(m.id);
    }
    std::sort(ids.begin(), ids.end()); // 与 query_all 一致的稳定顺序
    std::string json = entries_to_json(s, ids);
    diag_log(*s, "search: count=%zu", ids.size());
    return to_wcs(json);
}

#pragma endregion

#pragma region 保存 / 墓碑 / 诊断

KSBOX_API int ksbx_save(ksbx_store* s)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (s->indexDirty && !write_index(*s)) return KSBOX_ERR_IO;
    if (!write_data(*s)) return KSBOX_ERR_IO;
    if (!write_recovery(*s)) return KSBOX_ERR_IO;
    if (!write_tomb(*s)) return KSBOX_ERR_IO;
    if (tomb_over_limit(*s)) {
        // 墓碑超上限：压缩 data 并清空 tomb，回收空间
        diag_log(*s, "save: tomb_over_limit -> compact");
        if (!compact_data(*s)) return KSBOX_ERR_IO;
    }
    s->secretCache.clear(); // 保存后清空明文缓存（不长期驻留）
    s->removedIds.clear();
    diag_log(*s, "save: OK tombstones=%zu", s->removedIds.size());
    return KSBOX_OK;
}

KSBOX_API int ksbx_set_tomb_limit(ksbx_store* s, uint32_t maxBytes, uint32_t maxCount)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (maxBytes == 0 && maxCount == 0) return KSBOX_ERR_GENERIC; // 不允许两者同时无限制
    s->tombMaxBytes = maxBytes;
    s->tombMaxCount = maxCount;
    diag_log(*s, "set_tomb_limit: bytes=%u count=%u", maxBytes, maxCount);
    if (!write_settings(*s)) return KSBOX_ERR_IO; // 上限写入 settings 扩展区
    // 若已超限，立即压缩一次
    if (tomb_over_limit(*s)) {
        if (!compact_data(*s)) return KSBOX_ERR_IO;
    }
    return KSBOX_OK;
}

KSBOX_API int ksbx_get_tomb_limit(ksbx_store* s, uint32_t* outMaxBytes, uint32_t* outMaxCount)
{
    if (!s) return KSBOX_ERR_GENERIC;
    if (outMaxBytes) *outMaxBytes = s->tombMaxBytes;
    if (outMaxCount) *outMaxCount = s->tombMaxCount;
    return KSBOX_OK;
}

KSBOX_API int ksbx_get_diagnostics(ksbx_store* s, int* outEnabled)
{
    if (!s || !outEnabled) return KSBOX_ERR_GENERIC;
    *outEnabled = s->diag ? 1 : 0;
    return KSBOX_OK;
}

KSBOX_API int ksbx_set_diagnostics(ksbx_store* s, int enabled)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    s->diag = enabled != 0;
    // 开关先落 settings 扩展区（明文），下次 open 后立即生效
    if (!write_settings(*s)) return KSBOX_ERR_IO;
    diag_log(*s, "set_diagnostics: enabled=%d", s->diag ? 1 : 0);
    return KSBOX_OK;
}

#pragma endregion

KSBOX_API void ksbx_free(void* ptr)
{
    std::free(ptr);
}

} // extern "C"
