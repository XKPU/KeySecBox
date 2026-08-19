#pragma once

#include "keysecbox.h"
#include "crypto.h"
#include "json.hpp"

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

// KeySecBox 内部共享头：常量 / 结构体 / 跨 TU 函数声明。
// 拆分为 format(序列化) / persist(文件读写) / vault(对外 API) 三个 TU。

#pragma region 常量

static const long long UNCAT_ID = 0;
static const wchar_t* UNCAT_NAME = L"未分类";
static const uint8_t KDF_PBKDF2 = 1;               // master 中 kdf 字节
static const uint32_t PBKDF2_ITERATIONS = 600000;  // 防爆破（OWASP 2023 推荐）
// master 校验块解密后的期望明文
static const char* MASTER_CHECK = "KSX4-MASTER-OK";

// 各文件的 magic + 版本
static const char MAGIC_MASTER[4] = { 'K','S','X','M' }; // <base>.master 校验块+KDF
static const char MAGIC_ENTRIES[4] = { 'K','S','X','E' }; // <base>.entries 仅密码加密
static const char MAGIC_RECOVERY[4] = { 'K','S','X','R' }; // <base>.recovery

#pragma endregion

#pragma region 结构体

struct Category {
    long long id = 0;
    std::wstring name;
};

struct EntryMeta {
    long long id = 0;
    std::vector<long long> catIds; // 多分类；空数组时视为"未分类"
    // 新版账户/备注为明文，随 <base>.entries 记录直接读取，不参与加密。
    std::wstring account;
    std::wstring note;
};

struct SecretCache {
    std::wstring account;   // 明文仅瞬时驻留（新增/未保存编辑）
    std::wstring password;
    std::wstring note;
};

// entries/recovery 中一条密码密文（或恢复密钥密文）的定位
struct DataLoc {
    uint64_t offset = 0;
    uint32_t total = 0; // 整条记录字节数（不含文件头）
};

struct ksbx_store {
    // 路径
    std::wstring basePath;
    std::wstring prefsPath;
    std::wstring masterPath;
    std::wstring catsPath;
    std::wstring entriesPath;
    std::wstring mapPath;
    std::wstring recoveryPath;

    bool unlocked = false;
    bool metaDirty = false;
    bool recoveryDirty = false; // 恢复密钥有未写入变更

    std::vector<uint8_t> salt;
    uint32_t iterations = PBKDF2_ITERATIONS;
    std::vector<uint8_t> key;
    ksbx::crypto::GcmCtx gcm;  // 缓存的算法+密钥句柄
    std::vector<uint8_t> chkNonce, chkBlob; // master 校验块

    // 诊断日志开关（持久化于 <base>.prefs 明文）
    bool diag = false;

    std::unordered_map<long long, Category> categories;
    std::unordered_map<long long, EntryMeta> metas;
    std::unordered_map<long long, SecretCache> secretCache;
    std::unordered_map<long long, DataLoc> entriesLoc;
    std::unordered_map<long long, std::vector<long long>> catIndex;
    std::vector<long long> catOrder;

    std::vector<uint8_t> entriesFile;    // 最近读入/写入的 entries 文件
    std::vector<uint8_t> recoveryFile;   // 最近读入/写入的 recovery 文件

    // 恢复密钥（机密，逐条 AES-GCM；id 明文）
    std::unordered_map<long long, DataLoc> recoveryLoc;
    std::unordered_map<long long, std::vector<std::wstring>> recoveryCache; // 新增/修改项明文
    bool legacyMode = false;

    long long nextCatId = 1;
    long long nextEntryId = 1;

    // "全部"视图排序覆盖：仅被移动过的条目有固定位置（pin），其余按"分类序+分类内序"派生。
    // key=条目id, value=目标位置（0 基）。持久化于 <base>.map 的 pins 字段。
    std::unordered_map<long long, long long> allOrderPins;
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

// format.cpp：新版明文 JSON 序列化
std::string serialize_cats_doc(const ksbx_store& s);   // <base>.cats：分类数组（数组序=显示序）
bool deserialize_cats_doc(ksbx_store& s, const std::string& text);
std::string serialize_map_doc(const ksbx_store& s);    // <base>.map：计数器+分类内条目序+条目↔分类+pins
bool deserialize_map_doc(ksbx_store& s, const std::string& text);
std::string serialize_prefs_doc(const ksbx_store& s);  // <base>.prefs
bool deserialize_prefs_doc(ksbx_store& s, const std::string& text);

// format.cpp：条目/恢复记录流（二进制）
std::vector<uint8_t> build_entry_record(ksbx_store& s, long long id, const SecretCache& sc);
std::vector<uint8_t> build_recovery_record(ksbx_store& s, long long id,
                                           const std::vector<std::wstring>& keys);

// format.cpp：恢复密钥 JSON 数组
std::string serialize_recovery(const std::vector<std::wstring>& recovery);
void parse_recovery_input(const wchar_t* recoveryJson, std::vector<std::wstring>& out);
std::string serialize_cats(const std::vector<long long>& catIds); // JSON 数字数组

// persist.cpp：新版读写
bool file_exists(const std::wstring& path);
bool atomic_write_file(const std::wstring& path, const std::vector<uint8_t>& data);
bool read_file_bytes(const std::wstring& path, std::vector<uint8_t>& out);
bool encrypt_blob(ksbx_store& s, const std::string& plain,
                  std::vector<uint8_t>& out_nonce, std::vector<uint8_t>& out_blob);
bool decrypt_blob(ksbx_store& s, const std::vector<uint8_t>& nonce,
                  const std::vector<uint8_t>& blob, std::string& out_plain);
bool derive_for_store(ksbx_store& s, const std::wstring& masterPwd);
bool verify_password(ksbx_store& s);

bool load_prefs(ksbx_store& s);
bool load_master(ksbx_store& s);   // 必须：盐+KDF+校验块
bool load_cats(ksbx_store& s);
bool load_map(ksbx_store& s);      // 含分类内条目序（catIndex）
bool load_entries(ksbx_store& s);
bool load_recovery(ksbx_store& s);

bool write_prefs(ksbx_store& s);
bool write_master(ksbx_store& s);  // setup / change_password
bool write_cats(ksbx_store& s);
bool write_map(ksbx_store& s);     // 含分类内条目序（catIndex）
bool write_entries(ksbx_store& s);
bool write_recovery(ksbx_store& s);

// persist.cpp：旧版 1.0.x 读取（仅供 ksbx_open_legacy 导入使用）
int load_settings_legacy(ksbx_store& s, const std::wstring& settingsPath);
int load_index_legacy(ksbx_store& s, const std::wstring& indexPath);
int load_data_legacy(ksbx_store& s, const std::wstring& dataPath);
bool load_recovery_legacy(ksbx_store& s, const std::wstring& recoveryPath);

// vault.cpp：全部视图默认顺序（分类序 + 分类内条目序；多分类条目归入面板上最靠前的分类）
std::vector<long long> default_all_order(const ksbx_store& s);
// vault.cpp：在默认序上叠加 allOrderPins（仅被移动条目固定位置，其余按默认序填充）
std::vector<long long> build_all_with_pins(const ksbx_store& s);
void remove_from_all_order(ksbx_store& s, long long id); // 移除条目的全部视图 pin
void ensure_uncat(ksbx_store& s); // 保证内置"未分类"(id=0) 存在且恒居 catOrder 首位

#pragma endregion
