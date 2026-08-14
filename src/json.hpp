#pragma once
#include <string>
#include <vector>
#include <unordered_map>
#include <cstdint>

// 极简 JSON：仅支持本应用所需结构（对象/数组/字符串/数字/id）
// 用于 C/C++ 侧序列化库到字符串，交给 C# 解析（C# 侧用 System.Text.Json）
namespace ksbx {
namespace json {

// 转义字符串为 JSON 字符串字面量（含引号）
std::string escape(const std::wstring& s);
std::wstring unescape(const std::string& s);

// 简易解析器：把 JSON 解析为树
struct Value {
    enum Type { Null, Str, Num, Obj, Arr } type = Null;
    std::string str;                       // Str
    double num = 0;                         // Num
    std::unordered_map<std::string, Value> obj; // Obj
    std::vector<Value> arr;                // Arr
};

// 解析整个文档；失败返回 type==Null 且 ok=false
Value parse(const std::string& text, bool& ok);

// 从对象取字符串字段，缺失返回空
std::wstring get_str(const Value& v, const std::string& key);
long long get_i64(const Value& v, const std::string& key);

} // namespace json
} // namespace ksbx
