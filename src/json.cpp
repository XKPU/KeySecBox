#include "json.hpp"
#include <windows.h>
#include <string>
#include <vector>
#include <cstdio>
#include <cctype>

namespace ksbx {
namespace json {

static std::string wstr_to_utf8(const std::wstring& s)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(n - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), -1, &out[0], n, nullptr, nullptr);
    return out;
}

static std::wstring utf8_to_wstr(const std::string& s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    if (n <= 0) return {};
    std::wstring out(n - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, &out[0], n);
    return out;
}

std::string escape(const std::wstring& s)
{
    std::string u = wstr_to_utf8(s);
    std::string out;
    out.reserve(u.size() + 2);
    out.push_back('"');
    for (char c : u) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    char buf[8];
                    snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                } else {
                    out.push_back(c);
                }
        }
    }
    out.push_back('"');
    return out;
}

static void append_utf8_codepoint(std::string& out, unsigned int cp)
{
    if (cp < 0x80) {
        out.push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
        out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
}

std::wstring unescape(const std::string& s)
{
    // s 为 JSON 字符串内容（已去引号）
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '\\' && i + 1 < s.size()) {
            char n = s[++i];
            switch (n) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    // \uXXXX；处理代理对（高低代理组合成补充平面码点）
                    if (i + 4 < s.size()) {
                        unsigned int cp = 0;
                        sscanf(s.substr(i + 1, 4).c_str(), "%4x", &cp);
                        if (cp >= 0xD800 && cp <= 0xDBFF &&
                            i + 10 < s.size() && s[i + 5] == '\\' && s[i + 6] == 'u') {
                            unsigned int lo = 0;
                            sscanf(s.substr(i + 7, 4).c_str(), "%4x", &lo);
                            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                                unsigned int c = 0x10000 +
                                    ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                append_utf8_codepoint(out, c);
                                i += 10; // 跳过 \uXXXX\uXXXX
                                break;
                            }
                        }
                        append_utf8_codepoint(out, cp);
                        i += 4;
                    }
                    break;
                }
                default: out += n;
            }
        } else {
            out += s[i];
        }
    }
    return utf8_to_wstr(out);
}

// ---- 解析器 ----
struct Parser {
    const std::string& t;
    size_t i = 0;
    Parser(const std::string& text) : t(text) {}
    void skip() { while (i < t.size() && (t[i] == ' ' || t[i] == '\t' || t[i] == '\n' || t[i] == '\r')) ++i; }
    bool eat(char c) { skip(); if (i < t.size() && t[i] == c) { ++i; return true; } return false; }

    Value parse_value() {
        skip();
        Value v;
        if (i >= t.size()) return v;
        char c = t[i];
        if (c == '"') { v.type = Value::Str; v.str = parse_string(); }
        else if (c == '{') { v = parse_object(); }
        else if (c == '[') { v = parse_array(); }
        else if (c == 't' || c == 'f') { v.type = Value::Num; v.num = parse_bool(); }
        else if (c == 'n') { i += 4; v.type = Value::Null; }
        else { v.type = Value::Num; v.num = parse_number(); }
        return v;
    }

    std::string parse_string() {
        // 假设已定位在 '
        ++i; // 跳过 "
        std::string out;
        while (i < t.size() && t[i] != '"') {
            if (t[i] == '\\') {
                out += '\\';
                out += (++i < t.size()) ? t[i] : '\0';
                ++i;
            } else {
                out += t[i++];
            }
        }
        ++i; // 跳过闭合 "
        return out;
    }

    double parse_number() {
        size_t start = i;
        while (i < t.size() && (isdigit(t[i]) || t[i] == '-' || t[i] == '.' || t[i] == 'e' || t[i] == 'E' || t[i] == '+')) ++i;
        return atof(t.substr(start, i - start).c_str());
    }

    double parse_bool() {
        if (t.compare(i, 4, "true") == 0) { i += 4; return 1; }
        i += 5; return 0;
    }

    Value parse_object() {
        Value v; v.type = Value::Obj;
        ++i; // {
        skip();
        if (eat('}')) return v;
        while (true) {
            skip();
            if (t[i] != '"') break;
            std::string key = parse_string();
            skip();
            if (!eat(':')) break;
            Value val = parse_value();
            v.obj[key] = std::move(val);
            skip();
            if (eat(',')) continue;
            if (eat('}')) break;
            break;
        }
        return v;
    }

    Value parse_array() {
        Value v; v.type = Value::Arr;
        ++i; // [
        skip();
        if (eat(']')) return v;
        while (true) {
            Value val = parse_value();
            v.arr.push_back(std::move(val));
            skip();
            if (eat(',')) continue;
            if (eat(']')) break;
            break;
        }
        return v;
    }
};

Value parse(const std::string& text, bool& ok)
{
    Parser p(text);
    Value v = p.parse_value();
    ok = (v.type != Value::Null) || text.find_first_not_of(" \t\r\n") == std::string::npos;
    // 容错：空文本视为 ok
    if (text.empty()) ok = true;
    return v;
}

std::wstring get_str(const Value& v, const std::string& key)
{
    auto it = v.obj.find(key);
    if (it != v.obj.end() && it->second.type == Value::Str) return unescape(it->second.str);
    return {};
}

long long get_i64(const Value& v, const std::string& key)
{
    auto it = v.obj.find(key);
    if (it != v.obj.end() && it->second.type == Value::Num) return static_cast<long long>(it->second.num);
    return 0;
}

} // namespace json
} // namespace ksbx
