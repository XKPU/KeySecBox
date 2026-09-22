#pragma once
#include <string>
#include <vector>
#include <unordered_map>
#include <cstdint>

// 极简 JSON 解析器
namespace ksbx {
namespace json {

#pragma region 序列化

// 转义字符串为 JSON 字符串字面量（含引号）
std::string escape(const std::wstring& s);
std::wstring unescape(const std::string& s);

#pragma endregion

#pragma region 解析

struct Value {
    enum Type { Null, Str, Num, Obj, Arr } type = Null;
    std::string str;                       // Str
    double num = 0;                        // Num
    std::unordered_map<std::string, Value> obj; // Obj
    std::vector<Value> arr;                // Arr
};

// 解析整个文档；失败返回 type==Null 且 ok=false
Value parse(const std::string& text, bool& ok);

// 取对象字段，缺失返回空/0
std::wstring get_str(const Value& v, const std::string& key);
long long get_i64(const Value& v, const std::string& key);

#pragma endregion

} // namespace json
} // namespace ksbx
