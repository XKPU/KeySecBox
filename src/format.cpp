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

#pragma region index 序列化

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
        snprintf(buf, sizeof(buf), "{\"id\":%lld,\"catId\":%lld,\"hasNote\":", m.id, m.categoryId);
        out += buf;
        out += m.hasNote ? "1" : "0";
        out += "}";
    }
    out += "]}";
    diag_log(s, "serialize_index: cats=%zu items=%zu", s.categories.size(), s.metas.size());
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
    diag_log(s, "deserialize_index: OK cats=%zu items=%zu", s.categories.size(), s.metas.size());
    return true;
}

#pragma endregion

#pragma region recovery / secret 序列化

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

std::string serialize_secret(const std::wstring& account, const std::wstring& password, const std::wstring& note)
{
    std::string out = "{\"account\":";
    out += escape(account);
    out += ",\"password\":";
    out += escape(password);
    out += ",\"note\":";
    out += escape(note);
    out += "}";
    return out;
}

void deserialize_secret(const std::string& text, std::wstring& account, std::wstring& password, std::wstring& note)
{
    bool ok = false;
    Value v = parse(text, ok);
    if (ok && v.type == Value::Obj) {
        account = get_str(v, "account");
        password = get_str(v, "password");
        note = get_str(v, "note");
    }
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
