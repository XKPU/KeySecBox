#include "internal.h"

#include <windows.h>
#include <string>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <map>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <cwctype>

using namespace ksbx::json;

// 多文件格式 (KSX4)，均位于同一目录，basename 由 file 参数推导：
//   <base>.prefs     1. 偏好设置（明文记录）
//   <base>.master    2. 校验块 + KDF 参数（二进制）
//   <base>.cats      3. 分类：id+name（明文记录）
//   <base>.entries   4. 密码条目：密码经 AES-GCM 加密（二进制记录流）
//   <base>.map       5. 分类↔条目关联 + 分类内条目序 + 计数器 + 全部视图 pins（明文记录）
//   <base>.recovery  7. 恢复密钥：id 明文 + 密钥内容 AES-GCM 加密（二进制记录流）
//
// 密钥派生：解密密钥 = KDF(密码, 盐)。盐等"加密证书"存于 master。
// 内置分类"未分类" id=0，setup 自动建立，不可删除/重命名。

#pragma region 内部工具

static void to_lower(std::wstring& s)
{
    std::transform(s.begin(), s.end(), s.begin(), ::towlower);
}

static std::wstring path_with_ext(const std::wstring& base, const wchar_t* ext)
{
    return base + ext;
}

static void index_meta(ksbx_store& s, const EntryMeta& m)
{
    s.metas[m.id] = m;
    for (long long cid : m.catIds)
        s.catIndex[cid].push_back(m.id);
    if (m.id >= s.nextEntryId) s.nextEntryId = m.id + 1;
}

// 从 C# 传入的 JSON 数字数组解析分类 id 列表；空/null 得空
static void parse_cats_input(const wchar_t* catsJson, std::vector<long long>& out)
{
    out.clear();
    if (!catsJson || !*catsJson) return;
    int n = WideCharToMultiByte(CP_UTF8, 0, catsJson, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return;
    std::string utf8(n - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, catsJson, -1, &utf8[0], n, nullptr, nullptr);
    bool ok = false;
    Value v = parse(utf8, ok);
    if (ok && v.type == Value::Arr) {
        for (const auto& e : v.arr)
            if (e.type == Value::Num) out.push_back((long long)e.num);
    }
}

// 去除非法/重复分类并保证非空（为空时归入未分类）
static std::vector<long long> sanitize_cats(ksbx_store& s, std::vector<long long> cats)
{
    std::vector<long long> out;
    for (long long cid : cats) {
        if (s.categories.find(cid) == s.categories.end()) continue;
        if (std::find(out.begin(), out.end(), cid) != out.end()) continue;
        out.push_back(cid);
    }
    if (out.empty()) out.push_back(UNCAT_ID);
    return out;
}

// "全部"视图默认顺序 = 分类序 + 分类内条目序。
std::vector<long long> default_all_order(const ksbx_store& s)
{
    std::unordered_map<long long, size_t> catRank;
    for (size_t i = 0; i < s.catOrder.size(); ++i) catRank[s.catOrder[i]] = i;
    auto group_of = [&](const EntryMeta& m) -> long long {
        long long best = UNCAT_ID;
        size_t bestRank = SIZE_MAX;
        for (long long cid : m.catIds) {
            auto it = catRank.find(cid);
            if (it == catRank.end()) continue;
            if (it->second < bestRank) { bestRank = it->second; best = cid; }
        }
        return best;
    };

    std::vector<long long> out;
    out.reserve(s.metas.size());
    for (long long cid : s.catOrder) {
        auto idxIt = s.catIndex.find(cid);
        if (idxIt == s.catIndex.end()) continue;
        for (long long eid : idxIt->second) {
            auto mit = s.metas.find(eid);
            if (mit == s.metas.end()) continue;
            if (group_of(mit->second) == cid) out.push_back(eid);
        }
    }
    // 任何未被分组归入的条目（异常数据）按 id 增序补在末尾
    std::vector<long long> rest;
    for (const auto& kv : s.metas)
        if (std::find(out.begin(), out.end(), kv.first) == out.end())
            rest.push_back(kv.first);
    std::sort(rest.begin(), rest.end());
    out.insert(out.end(), rest.begin(), rest.end());
    return out;
}

// 在默认序上叠加全部视图 pins：仅被移动过的条目固定到指定位置，其余条目按默认序填充。
// pin 位置超出范围时 clamp；同位置多个 pin 按遍历顺序依次占位，后续自动后移。
std::vector<long long> build_all_with_pins(const ksbx_store& s)
{
    std::vector<long long> def = default_all_order(s);

    // 收集有效 pin：(position, entryId)，按位置排序
    std::vector<std::pair<long long, long long>> pins;
    for (const auto& kv : s.allOrderPins) {
        if (s.metas.find(kv.first) != s.metas.end())
            pins.emplace_back(kv.second, kv.first);
    }
    std::sort(pins.begin(), pins.end(),
              [](const auto& a, const auto& b) {
                  if (a.first != b.first) return a.first < b.first;
                  return a.second < b.second;
              });

    std::unordered_set<long long> pinIds;
    for (const auto& p : pins) pinIds.insert(p.second);

    // 非 pin 条目保持默认相对顺序
    std::vector<long long> nonPinned;
    nonPinned.reserve(def.size());
    for (long long id : def)
        if (pinIds.find(id) == pinIds.end()) nonPinned.push_back(id);

    size_t total = nonPinned.size() + pins.size();
    std::vector<long long> result;
    result.reserve(total);
    size_t npIdx = 0, pinIdx = 0;
    for (size_t i = 0; i < total; i++) {
        if (pinIdx < pins.size()) {
            long long pinPos = pins[pinIdx].first;
            if (pinPos < 0) pinPos = 0;
            if (pinPos >= (long long)total) pinPos = (long long)total - 1;
            if (pinPos <= (long long)i) {
                result.push_back(pins[pinIdx].second);
                pinIdx++;
                continue;
            }
        }
        if (npIdx < nonPinned.size())
            result.push_back(nonPinned[npIdx++]);
        else if (pinIdx < pins.size())
            result.push_back(pins[pinIdx++].second);
    }
    return result;
}

// 条目删除时移除其全部视图 pin（保持其余 pin 不变）
void remove_from_all_order(ksbx_store& s, long long id)
{
    auto it = s.allOrderPins.find(id);
    if (it != s.allOrderPins.end()) {
        s.allOrderPins.erase(it);
        s.metaDirty = true;
    }
}

// 保证内置"未分类"(id=0) 存在于 categories/catIndex，且恒定 catOrder 首位
void ensure_uncat(ksbx_store& s)
{
    if (s.categories.find(UNCAT_ID) == s.categories.end()) {
        Category uc; uc.id = UNCAT_ID; uc.name = UNCAT_NAME;
        s.categories[UNCAT_ID] = uc;
        s.catIndex[UNCAT_ID];
    }
    auto it = std::find(s.catOrder.begin(), s.catOrder.end(), UNCAT_ID);
    if (it == s.catOrder.end()) {
        s.catOrder.insert(s.catOrder.begin(), UNCAT_ID);
    } else if (it != s.catOrder.begin()) {
        s.catOrder.erase(it);
        s.catOrder.insert(s.catOrder.begin(), UNCAT_ID);
    }
}

static uint32_t rd_u32(const uint8_t* p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
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

// 旧版整条密文（account/password/note 同密文）解析
static void deserialize_secret_legacy(const std::string& text,
                                      std::wstring& account, std::wstring& password, std::wstring& note)
{
    bool ok = false;
    Value v = parse(text, ok);
    if (ok && v.type == Value::Obj) {
        account = get_str(v, "account");
        password = get_str(v, "password");
        note = get_str(v, "note");
    }
}

// 按需解密某条目恢复密钥。失败返回 false，成功填充 keys。
static bool decrypt_recovery_keys(ksbx_store& s, long long id, std::vector<std::wstring>& keys)
{
    auto locIt = s.recoveryLoc.find(id);
    if (locIt == s.recoveryLoc.end() || s.recoveryFile.empty()) return false;
    const auto& loc = locIt->second;
    if (loc.offset + loc.total > s.recoveryFile.size()) return false;
    const uint8_t* base = s.recoveryFile.data() + loc.offset;
    std::vector<uint8_t> nonce(base + 8, base + 20);
    uint32_t len = rd_u32(base + 20);
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

static wchar_t* to_wcs(const std::string& utf8)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, nullptr, 0);
    if (n <= 0) return nullptr;
    wchar_t* out = static_cast<wchar_t*>(std::malloc(n * sizeof(wchar_t)));
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, out, n);
    return out;
}

static bool peek_secret(ksbx_store& s, long long id,
                        std::wstring& account, std::wstring& password, std::wstring& note)
{
    auto cacheIt = s.secretCache.find(id);
    if (cacheIt != s.secretCache.end()) {
        account = cacheIt->second.account;
        password = cacheIt->second.password;
        note = cacheIt->second.note;
        return true;
    }
    auto locIt = s.entriesLoc.find(id);
    if (locIt == s.entriesLoc.end() || s.entriesFile.empty()) return false;
    const auto& loc = locIt->second;
    if (loc.offset + loc.total > s.entriesFile.size()) return false;
    const uint8_t* base = s.entriesFile.data() + loc.offset;

    if (s.legacyMode) {
        // 旧版记录布局
        std::vector<uint8_t> nonce(base + 8, base + 20);
        uint32_t len = rd_u32(base + 20);
        if (loc.total < 24 + len) return false;
        std::vector<uint8_t> cipher(base + 24, base + 24 + len);
        std::string plain;
        if (!decrypt_blob(s, nonce, cipher, plain)) return false;
        deserialize_secret_legacy(plain, account, password, note);
        std::fill(plain.begin(), plain.end(), '\0');
        return true;
    }

    // 新版记录布局
    size_t off = 8;
    uint32_t accLen = rd_u32(base + off); off += 4 + accLen;
    uint32_t noteLen = rd_u32(base + off); off += 4 + noteLen;
    if (off + 12 + 4 > loc.total) return false;
    std::vector<uint8_t> nonce(base + off, base + off + 12); off += 12;
    uint32_t pwLen = rd_u32(base + off); off += 4;
    if (off + pwLen > loc.total) return false;
    std::vector<uint8_t> cipher(base + off, base + off + pwLen);
    std::string plain;
    if (!decrypt_blob(s, nonce, cipher, plain)) return false;
    password = utf8_to_w(plain);
    std::fill(plain.begin(), plain.end(), '\0');
    auto mit = s.metas.find(id);
    if (mit != s.metas.end()) {
        account = mit->second.account;
        note = mit->second.note;
    }
    return true;
}

static bool meta_account_note(ksbx_store& s, long long id,
                              std::wstring& account, std::wstring& note)
{
    auto it = s.metas.find(id);
    if (it == s.metas.end()) return false;
    account = it->second.account;
    note = it->second.note;
    if (s.legacyMode) {
        std::wstring password;
        if (!peek_secret(s, id, account, password, note)) return false;
        if (note.empty()) note = it->second.note; // 旧库 index 明文备注兜底
    }
    return true;
}

// 列表/搜索：仅取明文账号/备注，密码不在此解密。
static std::string entries_to_json(ksbx_store* s, const std::vector<long long>& ids)
{
    std::string out = "[";
    bool first = true;
    for (long long id : ids) {
        auto it = s->metas.find(id);
        if (it == s->metas.end()) continue;
        const auto& m = it->second;
        std::wstring account, note;
        if (!meta_account_note(*s, id, account, note)) continue;
        if (!first) out += ",";
        first = false;
        char buf[96];
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"categoryId\":%lld,\"categoryIds\":", m.id,
                 m.catIds.empty() ? UNCAT_ID : m.catIds[0]);
        out += buf;
        out += serialize_cats(m.catIds);
        out += ",\"account\":";
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

static void set_paths(ksbx_store& s, const std::wstring& base)
{
    s.basePath = base;
    s.prefsPath = path_with_ext(base, L".prefs");
    s.masterPath = path_with_ext(base, L".master");
    s.catsPath = path_with_ext(base, L".cats");
    s.entriesPath = path_with_ext(base, L".entries");
    s.mapPath = path_with_ext(base, L".map");
    s.recoveryPath = path_with_ext(base, L".recovery");
}

KSBOX_API int ksbx_open(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd)
{
    if (!s || !file || !masterPwd) return KSBOX_ERR_GENERIC;
    set_paths(*s, file);

    // 自动验证库版本。
    if (!file_exists(s->masterPath)) {
        if (file_exists(s->basePath + L".settings")) {
            diag_log(*s, "open: LEGACY vault detected (v1.x)");
            return KSBOX_ERR_LEGACY;
        }
        return KSBOX_ERR_NO_VAULT;
    }

    if (!load_prefs(*s)) return KSBOX_ERR_IO;
    if (!load_master(*s)) return KSBOX_ERR_IO;
    if (!derive_for_store(*s, masterPwd)) return KSBOX_ERR_IO;
    if (!verify_password(*s)) {
        diag_log(*s, "open: WRONG_PASSWORD");
        return KSBOX_ERR_WRONG_PASSWORD;
    }
    if (!load_cats(*s)) return KSBOX_ERR_IO;
    if (!load_map(*s)) return KSBOX_ERR_IO;
    if (!load_entries(*s)) return KSBOX_ERR_IO;
    if (!load_recovery(*s)) return KSBOX_ERR_IO;

    s->unlocked = true;
    diag_log(*s, "open: OK cats=%zu entries=%zu dataLoc=%zu recLoc=%zu",
             s->categories.size(), s->metas.size(), s->entriesLoc.size(), s->recoveryLoc.size());
    return KSBOX_OK;
}

KSBOX_API int ksbx_setup(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd)
{
    if (!s || !file || !masterPwd || masterPwd[0] == L'\0') return KSBOX_ERR_GENERIC;
    if (file_exists(path_with_ext(file, L".master"))) return KSBOX_ERR_GENERIC; // 已存在新版库
    set_paths(*s, file);

    ksbx::crypto::random_bytes(s->salt, 16);
    s->iterations = PBKDF2_ITERATIONS;
    s->categories.clear();
    s->catIndex.clear();
    s->metas.clear();
    s->catOrder.clear();
    s->secretCache.clear();
    s->entriesLoc.clear();
    s->entriesFile.clear();
    s->recoveryLoc.clear();
    s->recoveryCache.clear();
    s->recoveryDirty = false;
    s->recoveryFile.clear();
    s->metaDirty = false;
    s->nextCatId = 1; s->nextEntryId = 1;
    s->allOrderPins.clear();
    s->diag = false;

    // 内置"未分类"
    ensure_uncat(*s);

    if (!derive_for_store(*s, masterPwd)) return KSBOX_ERR_IO;
    diag_log(*s, "setup: derived key, writing initial files");

    s->unlocked = true;
    if (!write_prefs(*s)) return KSBOX_ERR_IO;
    if (!write_master(*s)) return KSBOX_ERR_IO;
    if (!write_cats(*s)) return KSBOX_ERR_IO;
    if (!write_map(*s)) return KSBOX_ERR_IO;
    // 空 entries 文件：仅头（secretCache 为空时 write_entries 会跳过）
    std::vector<uint8_t> emptyHdr;
    emptyHdr.insert(emptyHdr.end(), MAGIC_ENTRIES, MAGIC_ENTRIES + 4);
    put_u32(emptyHdr, 1);
    if (!atomic_write_file(s->entriesPath, emptyHdr)) return KSBOX_ERR_IO;
    diag_log(*s, "setup: OK");
    return KSBOX_OK;
}

// 仅用于导入合并。
KSBOX_API int ksbx_open_legacy(ksbx_store* s, const wchar_t* legacyDir, const wchar_t* masterPwd)
{
    if (!s || !legacyDir || !masterPwd) return KSBOX_ERR_GENERIC;
    std::wstring dir = legacyDir;
    while (!dir.empty() && (dir.back() == L'\\' || dir.back() == L'/'))
        dir.pop_back(); // 容忍尾部斜杠
    std::wstring base = dir + L"\\vault";
    set_paths(*s, base);

    int rc = load_settings_legacy(*s, path_with_ext(base, L".settings"));
    if (rc != KSBOX_OK) return rc;
    s->legacyMode = true;
    if (!derive_for_store(*s, masterPwd)) return KSBOX_ERR_IO;
    if (!verify_password(*s)) {
        diag_log(*s, "open_legacy: WRONG_PASSWORD");
        return KSBOX_ERR_WRONG_PASSWORD;
    }
    rc = load_index_legacy(*s, path_with_ext(base, L".index"));
    if (rc != KSBOX_OK) { diag_log(*s, "open_legacy: load_index rc=%d", rc); return rc; }
    rc = load_data_legacy(*s, path_with_ext(base, L".data"));
    if (rc != KSBOX_OK) { diag_log(*s, "open_legacy: load_data rc=%d", rc); return rc; }
    if (!load_recovery_legacy(*s, path_with_ext(base, L".recovery"))) return KSBOX_ERR_IO;

    s->unlocked = true;
    diag_log(*s, "open_legacy: OK cats=%zu entries=%zu recLoc=%zu",
             s->categories.size(), s->metas.size(), s->recoveryLoc.size());
    return KSBOX_OK;
}

// 校验旧密码
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
    if (s->legacyMode) return KSBOX_ERR_GENERIC; // 只读旧版库禁止改密
    if (!newMasterPwd || newMasterPwd[0] == L'\0') return KSBOX_ERR_GENERIC;

    // 完全解密验证
    std::unordered_map<long long, SecretCache> all;
    for (const auto& kv : s->metas) {
        std::wstring account, password, note;
        if (!peek_secret(*s, kv.first, account, password, note)) return KSBOX_ERR_IO;
        all[kv.first] = SecretCache{ account, password, note };
    }
    std::unordered_map<long long, std::vector<std::wstring>> newRecovery;
    for (const auto& kv : s->recoveryLoc) {
        std::vector<std::wstring> keys;
        if (!decrypt_recovery_keys(*s, kv.first, keys)) return KSBOX_ERR_IO;
        newRecovery[kv.first] = std::move(keys);
    }

    // 换 key 重加密
    auto oldSalt = s->salt;
    auto oldKey = s->key;
    std::vector<uint8_t> bakEntries = s->entriesFile;
    std::vector<uint8_t> bakRecovery = s->recoveryFile;

    // 切换会话到新密钥
    ksbx::crypto::random_bytes(s->salt, 16);
    if (!derive_for_store(*s, newMasterPwd)) {
        s->salt = oldSalt;
        return KSBOX_ERR_IO;
    }
    s->secretCache = std::move(all);      // 全部条目重加密
    s->recoveryCache = std::move(newRecovery);
    s->recoveryDirty = true;

    bool okE = write_entries(*s);
    bool okR = okE && write_recovery(*s);
    bool okM = okR && write_master(*s);
    if (okM) {
        diag_log(*s, "change_password: OK entries=%zu", s->metas.size());
        return KSBOX_OK;
    }

    // 回滚
    if (okE) atomic_write_file(s->entriesPath, bakEntries);   // 磁盘被新 key 覆盖的恢复
    if (okR) atomic_write_file(s->recoveryPath, bakRecovery);
    s->salt = oldSalt;
    s->key = oldKey;
    s->gcm.init(oldKey);   // 重建旧 GCM 句柄（init 开头释放新句柄）
    s->secretCache.clear();
    s->recoveryCache.clear();
    s->recoveryDirty = false;
    load_entries(*s);      // 从（恢复后的）磁盘重载索引与明文账号/备注
    load_recovery(*s);
    diag_log(*s, "change_password: FAILED, rolled back (okE=%d okR=%d okM=%d)", okE, okR, okM);
    return KSBOX_ERR_IO;
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
    s->catOrder.push_back(c.id);
    s->metaDirty = true;
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
    s->metaDirty = true;
    diag_log(*s, "rename_category: OK id=%lld", id);
    return KSBOX_OK;
}

KSBOX_API int ksbx_move_category(ksbx_store* s, long long id, long long newPos)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (id == UNCAT_ID) return KSBOX_ERR_GENERIC; // 内置分类不可移动
    if (s->categories.find(id) == s->categories.end()) return KSBOX_ERR_NOT_FOUND;
    auto& v = s->catOrder;
    auto vit = std::find(v.begin(), v.end(), id);
    if (vit == v.end()) return KSBOX_ERR_NOT_FOUND;
    v.erase(vit);
    long long pos = newPos;
    if (pos < 1) pos = 1;  // "未分类"(id=0) 恒居首位
    if (pos > (long long)v.size()) pos = (long long)v.size();
    v.insert(v.begin() + pos, id);
    s->metaDirty = true; // 分类序变更（cats 数组序）
    diag_log(*s, "move_category: OK id=%lld pos=%lld", id, pos);
    return KSBOX_OK;
}

KSBOX_API int ksbx_remove_category(ksbx_store* s, long long id)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (id == UNCAT_ID) return KSBOX_ERR_GENERIC; // 内置分类不可删
    auto it = s->categories.find(id);
    if (it == s->categories.end()) return KSBOX_ERR_NOT_FOUND;
    auto idx = s->catIndex.find(id);
    std::vector<long long> doomed; // 删除分类后不再属于任何分类的条目
    if (idx != s->catIndex.end()) {
        for (long long eid : idx->second) {
            auto mit = s->metas.find(eid);
            if (mit == s->metas.end()) continue;
            auto& ids = mit->second.catIds;
            ids.erase(std::remove(ids.begin(), ids.end(), id), ids.end());
            if (ids.empty()) doomed.push_back(eid); // 多分类条目仅解除该分类
        }
        s->catIndex.erase(idx);
    }
    // 失去全部分类归属的条目一并删除
    for (long long eid : doomed) {
        remove_from_all_order(*s, eid);
        s->metas.erase(eid);
        s->secretCache.erase(eid);
        s->entriesLoc.erase(eid);
        if (s->recoveryLoc.erase(eid) > 0 || s->recoveryCache.erase(eid) > 0)
            s->recoveryDirty = true;
    }
    s->categories.erase(it);
    s->catOrder.erase(std::remove(s->catOrder.begin(), s->catOrder.end(), id), s->catOrder.end());
    s->metaDirty = true; // 分类/关联/分类内序变更（map 同步重写）
    diag_log(*s, "remove_category: OK id=%lld removedEntries=%zu", id, doomed.size());
    return KSBOX_OK;
}

KSBOX_API wchar_t* ksbx_list_categories(ksbx_store* s)
{
    if (!s || !s->unlocked) return nullptr;
    std::string out = "[";
    bool first = true;
    // 按 catOrder 顺序输出，保证与界面顺序一致
    for (long long cid : s->catOrder) {
        auto it = s->categories.find(cid);
        if (it == s->categories.end()) continue;
        const auto& c = it->second;
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

KSBOX_API long long ksbx_add_entry(ksbx_store* s, const wchar_t* categoryIdsJson,
    const wchar_t* account, const wchar_t* password, const wchar_t* note)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    std::vector<long long> cats;
    parse_cats_input(categoryIdsJson, cats);
    cats = sanitize_cats(*s, std::move(cats));
    EntryMeta m;
    m.id = s->nextEntryId++;
    m.catIds = cats;
    m.account = account ? account : L"";
    m.note = note ? note : L"";
    index_meta(*s, m);
    s->secretCache[m.id] = SecretCache{ m.account, password ? password : L"", m.note };
    s->metaDirty = true; // 关联变化（map 同步记录新条目）
    diag_log(*s, "add_entry: OK id=%lld cats=%zu", m.id, m.catIds.size());
    return m.id;
}

KSBOX_API int ksbx_update_entry(ksbx_store* s, long long id,
    const wchar_t* categoryIdsJson, const wchar_t* account, const wchar_t* password, const wchar_t* note)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    auto it = s->metas.find(id);
    if (it == s->metas.end()) return KSBOX_ERR_NOT_FOUND;

    std::vector<long long> cats;
    parse_cats_input(categoryIdsJson, cats);
    cats = sanitize_cats(*s, std::move(cats));

    // 从旧分类列表移除
    for (long long old : it->second.catIds) {
        auto& idx = s->catIndex[old];
        idx.erase(std::remove(idx.begin(), idx.end(), id), idx.end());
    }
    it->second.catIds = cats;
    for (long long cid : cats)
        s->catIndex[cid].push_back(id);

    it->second.account = account ? account : L"";
    it->second.note = note ? note : L"";
    s->secretCache[id] = SecretCache{ it->second.account, password ? password : L"", it->second.note };
    s->metaDirty = true; // 所属分类变化 → map 需同步
    diag_log(*s, "update_entry: OK id=%lld cats=%zu", id, cats.size());
    return KSBOX_OK;
}

KSBOX_API int ksbx_remove_entry(ksbx_store* s, long long id)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    auto it = s->metas.find(id);
    if (it == s->metas.end()) return KSBOX_ERR_NOT_FOUND;
    for (long long cid : it->second.catIds) {
        auto& idx = s->catIndex[cid];
        idx.erase(std::remove(idx.begin(), idx.end(), id), idx.end());
    }
    s->metas.erase(it);
    s->secretCache.erase(id);
    s->entriesLoc.erase(id);
    remove_from_all_order(*s, id);
    // 同步清除/计划清除其恢复密钥记录
    if (s->recoveryLoc.erase(id) > 0 || s->recoveryCache.erase(id) > 0)
        s->recoveryDirty = true;
    s->metaDirty = true; // 分类内条目序变更（map 同步剔除）
    diag_log(*s, "remove_entry: OK id=%lld", id);
    return KSBOX_OK;
}

KSBOX_API int ksbx_move_entry(ksbx_store* s, long long id, long long categoryId, long long newPos)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    auto it = s->metas.find(id);
    if (it == s->metas.end()) return KSBOX_ERR_NOT_FOUND;
    auto idxIt = s->catIndex.find(categoryId);
    if (idxIt == s->catIndex.end()) return KSBOX_ERR_NOT_FOUND;
    auto& v = idxIt->second;
    auto vit = std::find(v.begin(), v.end(), id);
    if (vit == v.end()) return KSBOX_ERR_NOT_FOUND; // 条目不属于该分类
    v.erase(vit);
    long long pos = newPos;
    if (pos < 0) pos = 0;
    if (pos > (long long)v.size()) pos = (long long)v.size();
    v.insert(v.begin() + pos, id);
    s->metaDirty = true; // 分类内条目序变更 → map 同步持久化
    diag_log(*s, "move_entry: OK id=%lld catId=%lld pos=%lld", id, categoryId, pos);
    return KSBOX_OK;
}

// 全部视图内移动：仅记录该条目的 pin（目标位置），其余条目仍按默认序排列。
KSBOX_API int ksbx_move_all_entry(ksbx_store* s, long long id, long long newPos)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (s->metas.find(id) == s->metas.end()) return KSBOX_ERR_NOT_FOUND;
    long long pos = newPos < 0 ? 0 : newPos;
    s->allOrderPins[id] = pos;
    s->metaDirty = true;
    diag_log(*s, "move_all_entry: OK id=%lld pos=%lld pins=%zu", id, pos, s->allOrderPins.size());
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
    snprintf(buf, sizeof(buf), "{\"id\":%lld,\"categoryId\":%lld,\"categoryIds\":", m.id,
             m.catIds.empty() ? UNCAT_ID : m.catIds[0]);
    std::string out = buf;
    out += serialize_cats(m.catIds);
    out += ",\"account\":"; out += escape(account);
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
    // 默认序 = 分类序 + 分类内条目序；allOrderPins 仅覆盖被移动过的条目位置
    std::vector<long long> ids = build_all_with_pins(*s);
    std::string json = entries_to_json(s, ids);
    diag_log(*s, "query_all: count=%zu pins=%zu", ids.size(), s->allOrderPins.size());
    return to_wcs(json);
}

KSBOX_API wchar_t* ksbx_query_category(ksbx_store* s, long long categoryId)
{
    if (!s || !s->unlocked) return nullptr;
    auto idx = s->catIndex.find(categoryId);
    if (idx == s->catIndex.end()) return to_wcs("[]");
    std::string json = entries_to_json(s, idx->second); // 直接引用，避免拷贝
    diag_log(*s, "query_category: OK catId=%lld count=%zu", categoryId, idx->second.size());
    return to_wcs(json);
}

KSBOX_API wchar_t* ksbx_search(ksbx_store* s, const wchar_t* keyword)
{
    if (!s || !s->unlocked) return nullptr;
    std::wstring kw = keyword ? keyword : L"";
    to_lower(kw);
    std::vector<long long> ids;
    for (const auto& kv : s->metas) {
        std::wstring account, note;
        if (!meta_account_note(*s, kv.first, account, note)) continue;
        std::wstring a = account;
        std::wstring n = note;
        to_lower(a);
        to_lower(n);
        if (a.find(kw) != std::wstring::npos || n.find(kw) != std::wstring::npos)
            ids.push_back(kv.first);
    }
    std::sort(ids.begin(), ids.end()); // 与 query_all 一致的稳定顺序
    std::string json = entries_to_json(s, ids);
    diag_log(*s, "search: count=%zu", ids.size());
    return to_wcs(json);
}

#pragma endregion

#pragma region 保存 / 诊断

KSBOX_API int ksbx_save(ksbx_store* s)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (s->legacyMode) return KSBOX_ERR_GENERIC; // 只读旧版库不可保存
    if (s->metaDirty) {
        if (!write_cats(*s)) return KSBOX_ERR_IO;
        if (!write_map(*s)) return KSBOX_ERR_IO;
        s->metaDirty = false;
    }
    if (!write_entries(*s)) return KSBOX_ERR_IO;
    if (!write_recovery(*s)) return KSBOX_ERR_IO;
    diag_log(*s, "save: OK");
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
    // 开关写入 <base>.prefs，下次 open 后立即生效
    if (!write_prefs(*s)) return KSBOX_ERR_IO;
    diag_log(*s, "set_diagnostics: enabled=%d", s->diag ? 1 : 0);
    return KSBOX_OK;
}

#pragma endregion

KSBOX_API void ksbx_free(void* ptr)
{
    std::free(ptr);
}

} // extern "C"
