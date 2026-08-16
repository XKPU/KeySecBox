#pragma once

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

// KeySecBox 内部共享头：常量 / 结构体 / 跨 TU 函数声明。
// 拆分为 format(序列化) / persist(文件读写) / vault(对外 API) 三个 TU。

#pragma region 常量

static const long long UNCAT_ID = 0;
static const wchar_t* UNCAT_NAME = L"未分类";
static const uint32_t TOMB_DEFAULT_MAX_BYTES = 15u * 1024u * 1024u; // 默认 15MB
static const uint32_t TOMB_DEFAULT_MAX_COUNT = 0; // 0 = 不按条数限制
static const uint8_t KDF_PBKDF2 = 1;               // settings 中 kdf 字节
static const uint32_t PBKDF2_ITERATIONS = 600000;  // 防爆破（OWASP 2023 推荐）

#pragma endregion

#pragma region 结构体

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
    std::wstring account;   // 明文仅瞬时驻留（新增/未保存编辑）
    std::wstring password;
    std::wstring note;      // 与账号密码同密文（data 记录）
};

// data/recovery 中一条有效密文的定位
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
    bool indexDirty = false;   // index 是否有未写入变更
    std::vector<uint8_t> salt;
    uint32_t iterations = PBKDF2_ITERATIONS;
    std::vector<uint8_t> key;
    ksbx::crypto::GcmCtx gcm;  // 缓存的算法+密钥句柄
    std::vector<uint8_t> chkNonce, chkBlob; // settings 校验块

    // 墓碑上限（0 表示不限该维度）
    uint32_t tombMaxBytes = TOMB_DEFAULT_MAX_BYTES;
    uint32_t tombMaxCount = TOMB_DEFAULT_MAX_COUNT;

    // 诊断日志开关
    bool diag = false;

    std::unordered_map<long long, Category> categories;
    std::unordered_map<long long, EntryMeta> metas;
    std::unordered_map<long long, SecretCache> secretCache;   // 新增/修改项明文
    std::unordered_map<long long, DataLoc> dataLoc;           // id -> 有效密文定位
    std::unordered_map<long long, std::vector<long long>> catIndex;

    std::vector<uint8_t> dataFile;     // 最近读入/写入的 data 文件
    std::vector<uint8_t> tombFile;     // 最近读入/写入的 tomb 文件
    std::vector<uint8_t> recoveryFile; // 最近读入/写入的 recovery 文件
    std::vector<long long> removedIds; // 本次会话删除的 id

    // 恢复密钥（机密，逐条 AES-GCM）
    std::unordered_map<long long, DataLoc> recoveryLoc;
    std::unordered_map<long long, std::vector<std::wstring>> recoveryCache; // 新增/修改项明文
    bool recoveryDirty = false;

    long long nextCatId = 1;
    long long nextEntryId = 1;
};

#pragma endregion

#pragma region 内部函数声明

// diag.cpp：诊断日志（仅 s.diag 开启时写 <base>.diag.log，只记非机密信息）
void diag_log(const ksbx_store& s, const char* fmt, ...);

// format.cpp：二进制小端读写
void put_u32(std::vector<uint8_t>& b, uint32_t v);
void put_u8(std::vector<uint8_t>& b, uint8_t v);
void put_i64(std::vector<uint8_t>& b, long long v);
bool get_u32(const std::vector<uint8_t>& b, size_t& p, uint32_t& out);
bool get_u8(const std::vector<uint8_t>& b, size_t& p, uint8_t& out);
bool get_i64(const std::vector<uint8_t>& b, size_t& p, long long& out);
bool get_bytes(const std::vector<uint8_t>& b, size_t& p, size_t n, std::vector<uint8_t>& out);

// format.cpp：JSON 序列化
std::string serialize_index(const ksbx_store& s);
bool deserialize_index(ksbx_store& s, const std::string& text);
std::string serialize_recovery(const std::vector<std::wstring>& recovery);
std::string serialize_secret(const std::wstring& account, const std::wstring& password, const std::wstring& note);
void deserialize_secret(const std::string& text, std::wstring& account, std::wstring& password, std::wstring& note);
void parse_recovery_input(const wchar_t* recoveryJson, std::vector<std::wstring>& out);

// persist.cpp：AES-GCM 记录流转接
bool encrypt_blob(ksbx_store& s, const std::string& plain,
                  std::vector<uint8_t>& out_nonce, std::vector<uint8_t>& out_blob);
bool decrypt_blob(ksbx_store& s, const std::vector<uint8_t>& nonce,
                  const std::vector<uint8_t>& blob, std::string& out_plain);
std::vector<uint8_t> build_secret_record(ksbx_store& s, long long id, const SecretCache* sc);
std::vector<uint8_t> build_recovery_record(ksbx_store& s, long long id,
                                           const std::vector<std::wstring>& keys);
bool scan_recovery_records(const std::vector<uint8_t>& blob,
                           std::unordered_map<long long, DataLoc>& out);

// persist.cpp：settings/index/data/tomb/recovery 读写
bool file_exists(const std::wstring& path);
bool write_settings(ksbx_store& s);
bool write_index(ksbx_store& s);
bool write_data(ksbx_store& s);
bool rebuild_data(ksbx_store& s);
bool write_tomb(ksbx_store& s);
bool tomb_over_limit(const ksbx_store& s);
bool load_tomb(ksbx_store& s);
bool compact_data(ksbx_store& s);
bool derive_for_store(ksbx_store& s, const std::wstring& masterPwd);
int load_settings(ksbx_store& s);
bool verify_password(ksbx_store& s);
int load_index(ksbx_store& s);
int load_data(ksbx_store& s);
bool load_recovery(ksbx_store& s);
bool write_recovery(ksbx_store& s);

#pragma endregion
