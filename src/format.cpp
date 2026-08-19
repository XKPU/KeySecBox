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

#pragma region 新版明文 JSON 序列化

// <base>.cats：分类数组，数组顺序即显示顺序（"未分类" id=0 恒居首位）。
std::string serialize_cats_doc(const ksbx_store& s)
{
    std::string out = "[";
    bool first = true;
    char buf[64];
    // 以 catOrder 顺序输出（未分类恒居首位）
    for (long long cid : s.catOrder) {
        auto it = s.categories.find(cid);
        if (it == s.categories.end()) continue;
        const auto& c = it->second;
        if (!first) out += ",";
        first = false;
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"name\":", c.id);
        out += buf;
        out += escape(c.name);
        out += "}";
    }
    out += "]";
    diag_log(s, "serialize_cats_doc: cats=%zu", s.categories.size());
    return out;
}

bool deserialize_cats_doc(ksbx_store& s, const std::string& text)
{
    bool ok = false;
    Value root = parse(text, ok);
    if (!ok || root.type != Value::Arr) return false;

    s.categories.clear();
    s.catOrder.clear();
    for (const auto& c : root.arr) {
        if (c.type != Value::Obj) continue;
        Category cat;
        cat.id = get_i64(c, "id");
        cat.name = get_str(c, "name");
        if (s.categories.find(cat.id) != s.categories.end()) continue; // 去重
        s.categories[cat.id] = cat;
        s.catOrder.push_back(cat.id);
        if (cat.id >= s.nextCatId) s.nextCatId = cat.id + 1;
    }
    // 确保内置"未分类"存在且恒居首位
    ensure_uncat(s);
    diag_log(s, "deserialize_cats_doc: OK cats=%zu", s.categories.size());
    return true;
}

// <base>.map：{"nextCatId":N,"nextEntryId":M,
//   "catIndex":{<cid>:[eid...]},
//   "entries":{<eid>:[cid...]},
//   "pins":{<eid>:<pos>}}
std::string serialize_map_doc(const ksbx_store& s)
{
    std::string out = "{\"nextCatId\":";
    char buf[64];
    snprintf(buf, sizeof(buf), "%lld,\"nextEntryId\":%lld,\"catIndex\":{", s.nextCatId, s.nextEntryId);
    out += buf;
    // 分类内条目序；按 catOrder（显示序）输出
    bool first = true;
    for (long long cid : s.catOrder) {
        auto it = s.catIndex.find(cid);
        if (it == s.catIndex.end()) continue;
        if (!first) out += ",";
        first = false;
        snprintf(buf, sizeof(buf), "\"%lld\":", cid);
        out += buf;
        out += serialize_cats(it->second);
    }
    out += "},\"entries\":{";
    // 条目 → 分类 id 数组；以条目 id 升序稳定输出
    std::vector<long long> ids;
    for (const auto& kv : s.metas) ids.push_back(kv.first);
    std::sort(ids.begin(), ids.end());
    first = true;
    for (long long eid : ids) {
        auto mit = s.metas.find(eid);
        if (mit == s.metas.end()) continue;
        if (!first) out += ",";
        first = false;
        snprintf(buf, sizeof(buf), "\"%lld\":", eid);
        out += buf;
        out += serialize_cats(mit->second.catIds);
    }
    out += "},\"pins\":{";
    // 全部视图 pins
    first = true;
    std::vector<std::pair<long long, long long>> pins(s.allOrderPins.begin(), s.allOrderPins.end());
    std::sort(pins.begin(), pins.end());
    for (const auto& pr : pins) {
        if (!first) out += ",";
        first = false;
        snprintf(buf, sizeof(buf), "\"%lld\":%lld", pr.first, pr.second);
        out += buf;
    }
    out += "}}";
    diag_log(s, "serialize_map_doc: entries=%zu pins=%zu", s.metas.size(), s.allOrderPins.size());
    return out;
}

bool deserialize_map_doc(ksbx_store& s, const std::string& text)
{
    bool ok = false;
    Value root = parse(text, ok);
    if (!ok || root.type != Value::Obj) return false;

    s.metas.clear();
    s.catIndex.clear();
    s.nextCatId = get_i64(root, "nextCatId");
    s.nextEntryId = get_i64(root, "nextEntryId");
    if (s.nextCatId < 1) s.nextCatId = 1;
    if (s.nextEntryId < 1) s.nextEntryId = 1;

    // 分类内条目序（catIndex）：防御性过滤，仅保留存在且仍属该分类的条目
    auto ci = root.obj.find("catIndex");
    if (ci != root.obj.end() && ci->second.type == Value::Obj) {
        for (const auto& kv : ci->second.obj) {
            long long cid = 0;
            bool bad = false;
            for (char ch : kv.first) {
                if (ch < '0' || ch > '9') { bad = true; break; }
                cid = cid * 10 + (ch - '0');
            }
            if (bad || s.categories.find(cid) == s.categories.end()) continue;
            std::vector<long long> ids;
            if (kv.second.type == Value::Arr) {
                for (const auto& e : kv.second.arr)
                    if (e.type == Value::Num) ids.push_back((long long)e.num);
            }
            // 去重
            std::vector<long long> uniq;
            for (long long eid : ids)
                if (std::find(uniq.begin(), uniq.end(), eid) == uniq.end())
                    uniq.push_back(eid);
            s.catIndex[cid] = std::move(uniq);
        }
    }
    // 为所有分类初始化槽位（含空分类与未分类）
    for (const auto& kv : s.categories)
        s.catIndex[kv.first];

    auto ei = root.obj.find("entries");
    if (ei != root.obj.end() && ei->second.type == Value::Obj) {
        for (const auto& kv : ei->second.obj) {
            long long eid = 0;
            bool bad = false;
            for (char ch : kv.first) {
                if (ch < '0' || ch > '9') { bad = true; break; }
                eid = eid * 10 + (ch - '0');
            }
            if (bad) continue;
            if (kv.second.type != Value::Arr) continue;
            EntryMeta m;
            m.id = eid;
            for (const auto& e : kv.second.arr)
                if (e.type == Value::Num) m.catIds.push_back((long long)e.num);
            if (m.id >= s.nextEntryId) s.nextEntryId = m.id + 1;
            s.metas[m.id] = std::move(m);
        }
    }

    // 全部视图 pins
    s.allOrderPins.clear();
    auto pi = root.obj.find("pins");
    if (pi != root.obj.end() && pi->second.type == Value::Obj) {
        for (const auto& kv : pi->second.obj) {
            long long eid = 0;
            bool bad = false;
            for (char ch : kv.first) {
                if (ch < '0' || ch > '9') { bad = true; break; }
                eid = eid * 10 + (ch - '0');
            }
            if (bad) continue;
            if (s.metas.find(eid) == s.metas.end()) continue;
            if (kv.second.type != Value::Num) continue;
            s.allOrderPins[eid] = (long long)kv.second.num;
        }
    }
    // catIndex 一致性过滤（O(M·K + ΣcatIndex)，map 自写自读正常情况为空操作）
    std::unordered_map<long long, std::unordered_set<long long>> member;
    for (const auto& kv : s.metas) {
        const auto& cs = kv.second.catIds;
        if (cs.empty()) member[UNCAT_ID].insert(kv.first);
        else for (long long cid : cs) member[cid].insert(kv.first);
    }
    for (auto& kv : s.catIndex) {
        long long cid = kv.first;
        auto mIt = member.find(cid);
        if (mIt == member.end()) { kv.second.clear(); continue; }
        std::vector<long long> valid;
        for (long long eid : kv.second)
            if (mIt->second.count(eid)) valid.push_back(eid);
        // 防御：缺失于 catIndex 但当前仍属该分类的条目按 id 升序补尾
        std::vector<long long> rest;
        for (long long eid : mIt->second)
            if (std::find(valid.begin(), valid.end(), eid) == valid.end())
                rest.push_back(eid);
        std::sort(rest.begin(), rest.end());
        valid.insert(valid.end(), rest.begin(), rest.end());
        kv.second = std::move(valid);
    }
    diag_log(s, "deserialize_map_doc: OK entries=%zu catIndex=%zu pins=%zu nextEntryId=%lld",
             s.metas.size(), s.catIndex.size(), s.allOrderPins.size(), s.nextEntryId);
    return true;
}

// <base>.prefs：偏好设置（明文，非机密）
std::string serialize_prefs_doc(const ksbx_store& s)
{
    char buf[48];
    snprintf(buf, sizeof(buf), "{\"diag\":%d}", s.diag ? 1 : 0);
    return buf;
}

bool deserialize_prefs_doc(ksbx_store& s, const std::string& text)
{
    bool ok = false;
    Value v = parse(text, ok);
    if (!ok || v.type != Value::Obj) return false;
    s.diag = (get_i64(v, "diag") != 0);
    return true;
}

#pragma endregion

#pragma region 恢复密钥 / 分类 id 数组

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

// 分类 id 数组序列化为 JSON 数字数组；空数组返回 "[]"
std::string serialize_cats(const std::vector<long long>& catIds)
{
    std::string out = "[";
    bool first = true;
    char buf[32];
    for (long long cid : catIds) {
        if (!first) out += ",";
        first = false;
        snprintf(buf, sizeof(buf), "%lld", cid);
        out += buf;
    }
    out += "]";
    return out;
}

// 解析 C# 传入的恢复密钥 JSON 数组（r="" 或 null 得空）
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

#pragma endregion

#pragma region 新版记录流构建（entries：仅密码加密；recovery：整块加密）

// entries 记录：id(i64) accLen(u32) account noteLen(u32) note nonce(12) pwLen(u32) pwdCipher+tag
std::vector<uint8_t> build_entry_record(ksbx_store& s, long long id, const SecretCache& sc)
{
    std::vector<uint8_t> rec;
    std::string accUtf8, noteUtf8;
    int na = WideCharToMultiByte(CP_UTF8, 0, sc.account.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (na > 1) { accUtf8.resize(na - 1); WideCharToMultiByte(CP_UTF8, 0, sc.account.c_str(), -1, &accUtf8[0], na, nullptr, nullptr); }
    int nn = WideCharToMultiByte(CP_UTF8, 0, sc.note.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (nn > 1) { noteUtf8.resize(nn - 1); WideCharToMultiByte(CP_UTF8, 0, sc.note.c_str(), -1, &noteUtf8[0], nn, nullptr, nullptr); }

    std::string pwdUtf8;
    int np = WideCharToMultiByte(CP_UTF8, 0, sc.password.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (np > 1) { pwdUtf8.resize(np - 1); WideCharToMultiByte(CP_UTF8, 0, sc.password.c_str(), -1, &pwdUtf8[0], np, nullptr, nullptr); }
    std::vector<uint8_t> pNonce, pBlob;
    if (!encrypt_blob(s, pwdUtf8, pNonce, pBlob)) return {};

    put_i64(rec, id);
    put_u32(rec, (uint32_t)accUtf8.size());
    rec.insert(rec.end(), accUtf8.begin(), accUtf8.end());
    put_u32(rec, (uint32_t)noteUtf8.size());
    rec.insert(rec.end(), noteUtf8.begin(), noteUtf8.end());
    rec.insert(rec.end(), pNonce.begin(), pNonce.end());
    put_u32(rec, (uint32_t)pBlob.size());
    rec.insert(rec.end(), pBlob.begin(), pBlob.end());
    return rec;
}

// recovery 记录：id(i64) nonce(12) len(u32) cipher+tag
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

#pragma endregion

#pragma region 二进制小端读写

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

#pragma endregion
