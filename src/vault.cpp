#include "keysecbox.h"
#include "crypto.h"
#include "json.hpp"

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

// ---------------------------------------------------------------------------
// 多文件格式 (KSX3)
//
// 文件均位于同一目录（运行目录），basename 由 ksbx_open/setup 的 file 参数推导：
//   <base>.settings   加密证书(盐+KDF类型与参数) + 校验块 + 扩展设置(JSON)
//   <base>.index      AES-GCM 整块加密：分类树 + 每条目轻量 meta 数组
//   <base>.data       AES-GCM 逐条独立加密（追加写，支持增量保存）
//   <base>.tomb       独立墓碑文件（定长记录，记已删除/失效的 id）
//   <base>.recovery   双重验证恢复密钥：AES-GCM 逐条独立加密（整文件重建，同 data 记录布局）
//
// [settings]
//   magic(4)='K','S','X','3'  ver(u32)
//   salt(16)  kdf(u8)=1(PBKDF2)  iterations(u32)
//   chkNonce(12) chkLen(u32) chkCipher(chkLen) chkTag(16)
//     chkCipher 解密为固定串 "KSX3-OK"（密码错误则 GCM tag 校验失败）
//   扩展设置 JSON（明文，非机密）: {"tombMaxBytes":N,"tombMaxCount":M}
// [index]  明文 JSON（不加密：分类与备注非机密，列表/搜索不解密）：
//   magic(4)='K','S','X','I'  ver(u32)
//   {"nextCatId":N,"nextEntryId":M,
//    "cats":[{"id":..,"name":..}],
//    "items":[{"id":..,"catId":..,"hasNote":0|1,"note":".."}]}
// [data]   记录流（追加写，仅账号+密码机密）：
//   magic(4)='K','S','X','D'  ver(u32)
//   每条记录: id(i64) nonce(12) len(u32) cipher(len) tag(16)   // 同 id 仅保留最后一条有效
//   密文内为 {"account":..,"password":..}
// [recovery] 记录流（整文件重建；恢复密钥与账号密码同级机密）：
//   magic(4)='K','S','X','R'  ver(u32)
//   每条记录: id(i64) nonce(12) len(u32) cipher(len) tag(16)
//   密文内为 ["k1","k2",...] JSON 字符串数组；无恢复密钥的条目不占记录
// [tomb]   定长记录流（16 字节/条）：
//   magic(4)='K','S','X','T'  ver(u32)
//   每条: id(i64) deleted(u8) reserved(7)
//
// 密钥派生：解密密钥 = KDF(用户输入密码, 盐)。盐等"加密证书"始终存于 settings。
//
// 机密内存策略（账号密码仅查询/编辑时瞬时解密）：
//   - 列表、搜索、分类、备注全部走明文 index，不解密任何条目
//   - secret（账号+密码）仅在 ksbx_get_entry 单条瞬时解密，JSON 返回后即弃
//   - 新增/编辑未保存前存于 secretCache，ksbx_save 后清空
//   - data 文件保存时「只追加」改动项密文，不重写历史 -> 增量保存，性能最优
//   - 墓碑独立文件，达到上限(大小/条数)时触发 data 压缩以回收空间
//
// 性能优化：
//   - AES-GCM 算法句柄 + 密钥句柄在解锁时缓存（GcmCtx），加解密不再重复 Open/Import
//   - data 文件真正增量追加（非整文件重写），并同步维护内存态，save 无需整体重读
//   - 墓碑追加后立即更新内存态，超限当次保存即触发压缩（不移除旧记录下次才补）
//
// 内置分类：未分类 id=0，setup 自动建立，不可删除/重命名。
// ---------------------------------------------------------------------------

static const long long UNCAT_ID = 0;
static const wchar_t* UNCAT_NAME = L"未分类";
static const uint32_t TOMB_DEFAULT_MAX_BYTES = 15u * 1024u * 1024u; // 默认 15MB
static const uint32_t TOMB_DEFAULT_MAX_COUNT = 0; // 0 = 不按条数限制
static const uint8_t KDF_PBKDF2 = 1;               // settings 中 kdf 字节
static const uint32_t PBKDF2_ITERATIONS = 600000;  // 防爆破（OWASP 2023 推荐）

struct Category {
    long long id = 0;
    std::wstring name;
};

struct EntryMeta {
    long long id = 0;
    long long categoryId = 0;
    std::wstring note;      // 明文（存于 index）
    bool hasNote = false;
};

struct SecretCache {
    std::wstring account;   // 明文仅瞬时驻留（查询/未保存编辑）
    std::wstring password;
};

// data 文件中一条有效密文的定位（取最后一条非墓碑记录）
struct DataLoc {
    uint64_t offset = 0;
    uint32_t total = 0; // 8(id)+12(nonce)+4(len)+cipher+16(tag)
};

struct ksbx_store {
    std::wstring basePath;     // 不含扩展名
    std::wstring settingsPath;
    std::wstring indexPath;
    std::wstring dataPath;
    std::wstring tombPath;
    std::wstring recoveryPath;

    bool unlocked = false;
    bool indexDirty = false;   // index 是否有未写入变更（避免无改动时的全量重写）
    std::vector<uint8_t> salt;
    uint32_t iterations = PBKDF2_ITERATIONS;
    std::vector<uint8_t> key;
    ksbx::crypto::GcmCtx gcm;  // 缓存的 AES-GCM 算法+密钥句柄（加解密性能关键）
    std::vector<uint8_t> chkNonce, chkBlob;   // settings 校验块（非机密，待解密）

    // 墓碑上限（由扩展设置载入；0 表示不限制该维度）
    uint32_t tombMaxBytes = TOMB_DEFAULT_MAX_BYTES;
    uint32_t tombMaxCount = TOMB_DEFAULT_MAX_COUNT;

    std::unordered_map<long long, Category> categories;
    std::unordered_map<long long, EntryMeta> metas;
    std::unordered_map<long long, SecretCache> secretCache;   // 仅新增/修改项明文
    std::unordered_map<long long, DataLoc> dataLoc;           // id -> 当前有效密文定位
    std::unordered_map<long long, std::vector<long long>> catIndex;

    std::vector<uint8_t> dataFile;   // 最近一次读入/写入的 data 文件（保存时用于拷贝未改动 blob）
    std::vector<uint8_t> tombFile;   // 最近一次读入/写入的 tomb 文件
    std::vector<uint8_t> recoveryFile; // 最近一次读入/写入的 recovery 文件
    std::vector<long long> removedIds; // 本次会话删除的 id（待写入 tomb 文件）

    // 恢复密钥独立存储（机密，逐条 AES-GCM）：
    std::unordered_map<long long, DataLoc> recoveryLoc;   // id -> 当前有效密文定位
    std::unordered_map<long long, std::vector<std::wstring>> recoveryCache; // 仅新增/修改项明文
    bool recoveryDirty = false;                            // recovery 有未写入变更

    long long nextCatId = 1;
    long long nextEntryId = 1;
};

namespace {

using namespace ksbx::json;

void to_lower(std::wstring& s)
{
    std::transform(s.begin(), s.end(), s.begin(), ::towlower);
}

std::wstring path_with_ext(const std::wstring& base, const wchar_t* ext)
{
    return base + ext;
}

bool file_exists(const std::wstring& path)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"rb") == 0 && f) { fclose(f); return true; }
    return false;
}

// ---- index 序列化 ----
std::string serialize_index(const ksbx_store& s)
{
    char buf[64];
    std::string out = "{";
    snprintf(buf, sizeof(buf), "\"nextCatId\":%lld,\"nextEntryId\":%lld,\"cats\":[",
             s.nextCatId, s.nextEntryId);
    out += buf;
    bool first = true;
    // 按 id 排序输出，保证确定性（"未分类" id=0 恒排最前）
    std::vector<long long> catIds;
    catIds.reserve(s.categories.size());
    for (const auto& kv : s.categories) catIds.push_back(kv.first);
    std::sort(catIds.begin(), catIds.end());
    for (long long cid : catIds) {
        const auto& c = s.categories.find(cid)->second;
        if (!first) out += ",";
        first = false;
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"name\":", c.id);
        out += buf;
        out += escape(c.name);
        out += "}";
    }
    out += "],\"items\":[";
    first = true;
    for (const auto& kv : s.metas) {
        const auto& m = kv.second;
        if (!first) out += ",";
        first = false;
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"catId\":%lld,\"note\":", m.id, m.categoryId);
        out += buf;
        out += escape(m.note);
        out += ",\"hasNote\":";
        out += m.hasNote ? "1" : "0";
        out += "}";
    }
    out += "]}";
    return out;
}

bool deserialize_index(ksbx_store& s, const std::string& text)
{
    bool ok = false;
    Value root = parse(text, ok);
    if (!ok || root.type != Value::Obj) return false;

    s.categories.clear();
    s.catIndex.clear();
    s.metas.clear();

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
        }
    }
    // 确保内置"未分类"存在
    if (s.categories.find(UNCAT_ID) == s.categories.end()) {
        Category uc; uc.id = UNCAT_ID; uc.name = UNCAT_NAME;
        s.categories[UNCAT_ID] = uc;
        s.catIndex[UNCAT_ID];
    }

    auto iit = root.obj.find("items");
    if (iit != root.obj.end() && iit->second.type == Value::Arr) {
        for (const auto& e : iit->second.arr) {
            EntryMeta m;
            m.id = get_i64(e, "id");
            m.categoryId = get_i64(e, "catId");
            m.note = get_str(e, "note");
            m.hasNote = (get_i64(e, "hasNote") != 0);
            if (s.categories.find(m.categoryId) == s.categories.end())
                m.categoryId = UNCAT_ID; // 分类丢失则归入未分类
            s.metas[m.id] = m;
            s.catIndex[m.categoryId].push_back(m.id);
            if (m.id >= s.nextEntryId) s.nextEntryId = m.id + 1;
        }
    }
    return true;
}

std::string serialize_recovery(const std::vector<std::wstring>& recovery)
{
    std::string out = "[";
    bool first = true;
    for (const auto& k : recovery) {
        if (!first) out += ",";
        first = false;
        out += escape(k);
    }
    out += "]";
    return out;
}

std::string serialize_secret(const std::wstring& account, const std::wstring& password)
{
    std::string out = "{\"account\":";
    out += escape(account);
    out += ",\"password\":";
    out += escape(password);
    out += "}";
    return out;
}

void deserialize_secret(const std::string& text, std::wstring& account, std::wstring& password)
{
    bool ok = false;
    Value v = parse(text, ok);
    if (ok && v.type == Value::Obj) {
        account = get_str(v, "account");
        password = get_str(v, "password");
    }
}

// 从 wchar_t* 输入解析恢复密钥 JSON（C# 传入的字符串数组），r="" 或 null 得空
void parse_recovery_input(const wchar_t* recoveryJson, std::vector<std::wstring>& out)
{
    out.clear();
    if (!recoveryJson || !*recoveryJson) return;
    int n = WideCharToMultiByte(CP_UTF8, 0, recoveryJson, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return;
    std::string utf8(n - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, recoveryJson, -1, &utf8[0], n, nullptr, nullptr);
    bool ok = false;
    Value v = parse(utf8, ok);
    if (ok && v.type == Value::Arr) {
        for (const auto& e : v.arr)
            if (e.type == Value::Str) out.push_back(unescape(e.str));
    }
}

void index_meta(ksbx_store& s, const EntryMeta& m)
{
    s.metas[m.id] = m;
    s.catIndex[m.categoryId].push_back(m.id);
    if (m.id >= s.nextEntryId) s.nextEntryId = m.id + 1;
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

void put_u32(std::vector<uint8_t>& b, uint32_t v)
{
    b.push_back((uint8_t)(v & 0xFF));
    b.push_back((uint8_t)((v >> 8) & 0xFF));
    b.push_back((uint8_t)((v >> 16) & 0xFF));
    b.push_back((uint8_t)((v >> 24) & 0xFF));
}
void put_u8(std::vector<uint8_t>& b, uint8_t v) { b.push_back(v); }
void put_i64(std::vector<uint8_t>& b, long long v)
{
    for (int i = 0; i < 8; i++) b.push_back((uint8_t)((v >> (8 * i)) & 0xFF));
}
bool get_u32(const std::vector<uint8_t>& b, size_t& p, uint32_t& out)
{
    if (p + 4 > b.size()) return false;
    out = (uint32_t)b[p] | ((uint32_t)b[p + 1] << 8) |
          ((uint32_t)b[p + 2] << 16) | ((uint32_t)b[p + 3] << 24);
    p += 4; return true;
}
bool get_u8(const std::vector<uint8_t>& b, size_t& p, uint8_t& out)
{
    if (p + 1 > b.size()) return false;
    out = b[p]; p += 1; return true;
}
bool get_i64(const std::vector<uint8_t>& b, size_t& p, long long& out)
{
    if (p + 8 > b.size()) return false;
    unsigned long long v = 0;
    for (int i = 0; i < 8; i++) v |= (unsigned long long)b[p + i] << (8 * i);
    out = (long long)v; p += 8; return true;
}
bool get_bytes(const std::vector<uint8_t>& b, size_t& p, size_t n, std::vector<uint8_t>& out)
{
    if (p + n > b.size()) return false;
    out.assign(b.begin() + p, b.begin() + p + n);
    p += n; return true;
}

// 构建单条 data 记录字节序列（含 id 头）。sc 为空则从 dataFile 原样拷贝该 id 当前密文
// （未改动项不解密，性能关键路径）。失败返回空 vector。
std::vector<uint8_t> build_secret_record(ksbx_store& s, long long id, const SecretCache* sc)
{
    std::vector<uint8_t> rec;
    if (sc) {
        std::string secPlain = serialize_secret(sc->account, sc->password);
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

bool write_settings(ksbx_store& s)
{
    std::vector<uint8_t> blob;
    blob.push_back('K'); blob.push_back('S'); blob.push_back('X'); blob.push_back('3');
    put_u32(blob, 2); // settings 格式版本 2（含 KDF 类型 + 扩展设置）
    for (uint8_t x : s.salt) blob.push_back(x);
    blob.push_back(KDF_PBKDF2);
    put_u32(blob, s.iterations);

    std::vector<uint8_t> cNonce, cBlob;
    if (!encrypt_blob(s, "KSX3-OK", cNonce, cBlob)) return false;
    for (uint8_t x : cNonce) blob.push_back(x);
    put_u32(blob, (uint32_t)cBlob.size());
    for (uint8_t x : cBlob) blob.push_back(x);

    // 扩展设置 JSON（明文、非机密）：墓碑上限
    char ebuf[96];
    snprintf(ebuf, sizeof(ebuf), "{\"tombMaxBytes\":%u,\"tombMaxCount\":%u}",
             s.tombMaxBytes, s.tombMaxCount);
    for (char c : std::string(ebuf)) blob.push_back((uint8_t)c);

    FILE* f = nullptr;
    if (_wfopen_s(&f, s.settingsPath.c_str(), L"wb") != 0 || !f) return false;
    size_t w = fwrite(blob.data(), 1, blob.size(), f);
    fclose(f);
    return w == blob.size();
}

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
    return true;
}

// data 文件：仅追加 secretCache 改动项（真正增量写，性能最优），
// 并同步更新内存态 dataLoc / dataFile，save 无需整体重读。
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
    return true;
}

// 从头重建 data 文件（初始设置/换密码/压缩时使用），并更新内存态 dataLoc / dataFile。
// 跳过已被墓碑标记（已删除）的 id —— 即"移除最旧条目，回收空间"。
bool rebuild_data(ksbx_store& s)
{
    std::vector<uint8_t> out;
    const uint8_t hdr[8] = { 'K','S','X','D', 2,0,0,0 };
    out.insert(out.end(), hdr, hdr + 8);
    s.dataLoc.clear();

    for (const auto& kv : s.metas) {
        SecretCache* sc = nullptr;
        auto c = s.secretCache.find(kv.first);
        if (c != s.secretCache.end()) sc = &c->second;
        std::vector<uint8_t> rec = build_secret_record(s, kv.first, sc);
        if (rec.empty()) return false;
        DataLoc loc; loc.offset = (uint64_t)out.size(); loc.total = (uint32_t)rec.size();
        s.dataLoc[kv.first] = loc;
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
    return true;
}

// 墓碑文件：追加 removedIds 记录（定长 16 字节：id(8)+deleted(1)+reserved(7)），
// 并同步更新内存态 tombFile，使超限判断立即可靠。
bool write_tomb(ksbx_store& s)
{
    if (s.removedIds.empty()) return true; // 无需改动

    std::vector<uint8_t> recs;
    for (long long rid : s.removedIds) {
        put_i64(recs, rid);
        recs.push_back(1); // deleted
        recs.insert(recs.end(), 7, 0); // reserved -> 每条 16 字节
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
    return true;
}

// 墓碑是否已达上限（触发 data 压缩回收空间）
bool tomb_over_limit(const ksbx_store& s)
{
    if (s.tombMaxCount > 0 && (uint32_t)(s.tombFile.size() / 16) >= s.tombMaxCount) return true;
    if (s.tombMaxBytes > 0 && (uint32_t)s.tombFile.size() >= s.tombMaxBytes) return true;
    return false;
}

// 加载 tomb 文件，建立 removedIds 集合（当前已删除 id）
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
    return true;
}

// 压缩：重建 data 文件为只含当前有效密文（排除已删除 id），并清空 tomb 文件。
// 内存态同步更新，save 后无需重读。
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
    return true;
}

// 用当前 store 的 salt + KDF 参数，按密码派生密钥并初始化 GCM 会话
// （解密密钥由密码+加密证书派生）
bool derive_for_store(ksbx_store& s, const std::wstring& masterPwd)
{
    s.key.assign(32, 0);
    if (!ksbx::crypto::derive_key(masterPwd, s.salt, s.iterations, s.key)) return false;
    return s.gcm.init(s.key);
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
    if (p + 16 > blob.size()) return KSBOX_ERR_IO; // 越界保护
    s.salt.assign(blob.begin() + p, blob.begin() + p + 16); p += 16;
    if (ver >= 2) {
        uint8_t kdf = 0;
        if (!get_u8(blob, p, kdf)) return KSBOX_ERR_IO;
        if (kdf != KDF_PBKDF2) return KSBOX_ERR_IO; // 不支持其他 KDF（旧 Argon2id 库）
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
        }
    }
    return KSBOX_OK;
}

// 校验密码：用当前已派生的 s.key 解密 settings 校验块（密码错误则 GCM tag 失败）
bool verify_password(ksbx_store& s)
{
    std::string chk;
    if (!decrypt_blob(s, s.chkNonce, s.chkBlob, chk)) return false;
    return chk == "KSX3-OK";
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
    return KSBOX_OK;
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
    if (ver != 2) return KSBOX_ERR_IO; // 旧格式（secret 含 note）不支持

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
    return KSBOX_OK;
}

// ---- 恢复密钥独立存储（<base>.recovery）----
// 机密性等同账号/密码：逐条 AES-GCM，明文仅在查询/编辑时瞬时驻留 recoveryCache。
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

// 解析 recovery 记录流，重建 id -> 定位 映射（同 id 仅保留最后一条）
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
    return scan_recovery_records(s.recoveryFile, s.recoveryLoc);
}

// 重建 recovery 文件并原子替换：未改动记录原样拷贝，改动项重加密，已删除(不在 loc)项消失。
// recoveryCache 中的明文保存后即清（对应"未保存前存于 cache，save 后清空"的策略）。
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
    return true;
}

// 按需解密某条目恢复密钥（从 recovery 记录）。失败返回 false，成功填充 keys。
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

// 按需解密某条目 secret（账号+密码）。返回是否成功。明文用完即弃，不驻留。
bool peek_secret(ksbx_store& s, long long id, std::wstring& account, std::wstring& password)
{
    auto cacheIt = s.secretCache.find(id);
    if (cacheIt != s.secretCache.end()) {
        account = cacheIt->second.account;
        password = cacheIt->second.password;
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
    deserialize_secret(plain, account, password);
    std::fill(plain.begin(), plain.end(), '\0'); // 抹除瞬态明文
    return true;
}

// 列表/搜索：仅明文元信息（分类、备注、id），不解密任何条目
std::string entries_to_json(ksbx_store* s, const std::vector<long long>& ids)
{
    std::string out = "[";
    bool first = true;
    for (long long id : ids) {
        auto it = s->metas.find(id);
        if (it == s->metas.end()) continue;
        const auto& m = it->second;
        if (!first) out += ",";
        first = false;
        char buf[96];
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"categoryId\":%lld,\"note\":", m.id, m.categoryId);
        out += buf;
        out += escape(m.note);
        out += "}";
    }
    out += "]";
    return out;
}

} // namespace

extern "C" {

KSBOX_API ksbx_store* ksbx_store_create()
{
    return new (std::nothrow) ksbx_store();
}

KSBOX_API void ksbx_store_destroy(ksbx_store* s)
{
    delete s; // GcmCtx 析构自动释放 BCrypt 句柄
}

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
    if (!derive_for_store(*s, masterPwd)) return KSBOX_ERR_IO;
    if (!verify_password(*s)) return KSBOX_ERR_WRONG_PASSWORD;
    rc = load_index(*s);
    if (rc != KSBOX_OK) return rc;
    rc = load_data(*s);
    if (rc != KSBOX_OK) return rc;
    if (!load_tomb(*s)) return KSBOX_ERR_IO;
    if (!load_recovery(*s)) return KSBOX_ERR_IO;
    s->indexDirty = false;
    s->unlocked = true;
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

    s->unlocked = true;
    if (!write_settings(*s)) return KSBOX_ERR_IO;
    if (!write_index(*s)) return KSBOX_ERR_IO;
    if (!rebuild_data(*s)) return KSBOX_ERR_IO;   // 建立空的 data 文件
    if (!write_tomb(*s)) return KSBOX_ERR_IO;
    return KSBOX_OK;
}

KSBOX_API int ksbx_change_password(ksbx_store* s, const wchar_t* newMasterPwd)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (!newMasterPwd || newMasterPwd[0] == L'\0') return KSBOX_ERR_GENERIC;
    // 先尝试用旧 key 解密全部 secret（低频主动操作，短暂驻留）。
    // 任一失败即中止，避免重加密后丢失数据。
    std::unordered_map<long long, SecretCache> all;
    for (const auto& kv : s->metas) {
        std::wstring account, password;
        if (!peek_secret(*s, kv.first, account, password)) return KSBOX_ERR_IO;
        all[kv.first] = SecretCache{ account, password };
    }
    // 恢复密钥同策略：先用旧 key 解出全部（短暂驻留），换钥后统一重加密
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
    if (!write_settings(*s)) return KSBOX_ERR_IO;
    if (!write_index(*s)) return KSBOX_ERR_IO;
    if (!rebuild_data(*s)) return KSBOX_ERR_IO; // 全部条目重加密，从头重建（丢弃旧密文）
    if (!write_recovery(*s)) return KSBOX_ERR_IO; // 全部恢复记录重加密
    if (!write_tomb(*s)) return KSBOX_ERR_IO;
    s->secretCache.clear();
    s->removedIds.clear();
    return KSBOX_OK;
}

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
    return to_wcs(out);
}

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
    s->secretCache[m.id] = SecretCache{ account ? account : L"", password ? password : L"" };
    s->indexDirty = true;
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
    s->secretCache[id] = SecretCache{ account ? account : L"", password ? password : L"" };
    s->indexDirty = true;
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
    return KSBOX_OK;
}

KSBOX_API wchar_t* ksbx_get_entry(ksbx_store* s, long long id)
{
    if (!s || !s->unlocked) return nullptr;
    auto it = s->metas.find(id);
    if (it == s->metas.end()) return nullptr;
    const auto& m = it->second;
    // 唯一解密入口：瞬时解密该条账号+密码，返回 JSON 后即弃
    std::wstring account, password;
    if (!peek_secret(*s, id, account, password)) return nullptr;

    char buf[96];
    snprintf(buf, sizeof(buf), "{\"id\":%lld,\"categoryId\":%lld,\"account\":", m.id, m.categoryId);
    std::string out = buf;
    out += escape(account);
    out += ",\"password\":"; out += escape(password);
    out += ",\"note\":"; out += escape(m.note);
    out += "}";
    return to_wcs(out);
}

KSBOX_API int ksbx_set_recovery(ksbx_store* s, long long id, const wchar_t* keysJson)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (s->metas.find(id) == s->metas.end()) return KSBOX_ERR_NOT_FOUND;
    std::vector<std::wstring> keys;
    parse_recovery_input(keysJson, keys);
    if (keys.empty()) {
        // 空数组 = 删除该条恢复记录（下次 save 重建时报废）
        if (s->recoveryLoc.erase(id) > 0 || s->recoveryCache.erase(id) > 0)
            s->recoveryDirty = true;
        return KSBOX_OK;
    }
    s->recoveryCache[id] = std::move(keys);
    s->recoveryDirty = true;
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
    return to_wcs(serialize_recovery(keys));
}

KSBOX_API wchar_t* ksbx_query_all(ksbx_store* s)
{
    if (!s || !s->unlocked) return nullptr;
    std::vector<long long> ids;
    ids.reserve(s->metas.size());
    for (const auto& kv : s->metas) ids.push_back(kv.first);
    return to_wcs(entries_to_json(s, ids));
}

KSBOX_API wchar_t* ksbx_query_category(ksbx_store* s, long long categoryId)
{
    if (!s || !s->unlocked) return nullptr;
    auto idx = s->catIndex.find(categoryId);
    std::vector<long long> ids = (idx != s->catIndex.end()) ? idx->second : std::vector<long long>{};
    return to_wcs(entries_to_json(s, ids));
}

KSBOX_API wchar_t* ksbx_search(ksbx_store* s, const wchar_t* keyword)
{
    if (!s || !s->unlocked) return nullptr;
    std::wstring kw = keyword ? keyword : L"";
    to_lower(kw);
    std::vector<long long> ids;
    for (const auto& kv : s->metas) {
        const auto& m = kv.second;
        std::wstring n = m.note;
        to_lower(n);
        if (n.find(kw) != std::wstring::npos) ids.push_back(m.id);
    }
    return to_wcs(entries_to_json(s, ids));
}

KSBOX_API int ksbx_save(ksbx_store* s)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (s->indexDirty && !write_index(*s)) return KSBOX_ERR_IO;
    if (!write_data(*s)) return KSBOX_ERR_IO;
    if (!write_recovery(*s)) return KSBOX_ERR_IO;
    if (!write_tomb(*s)) return KSBOX_ERR_IO;
    if (tomb_over_limit(*s)) {
        // 墓碑超上限：压缩 data 并清空 tomb，回收空间（移除最旧条目 + 容纳新条目）
        if (!compact_data(*s)) return KSBOX_ERR_IO;
    }
    s->secretCache.clear(); // 保存后清空明文缓存（不长期驻留）
    s->removedIds.clear();
    return KSBOX_OK;
}

KSBOX_API int ksbx_set_tomb_limit(ksbx_store* s, uint32_t maxBytes, uint32_t maxCount)
{
    if (!s || !s->unlocked) return KSBOX_ERR_NOT_UNLOCKED;
    if (maxBytes == 0 && maxCount == 0) return KSBOX_ERR_GENERIC; // 不允许两者同时无限制
    s->tombMaxBytes = maxBytes;
    s->tombMaxCount = maxCount;
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

KSBOX_API void ksbx_free(void* ptr)
{
    std::free(ptr);
}

} // extern "C"
