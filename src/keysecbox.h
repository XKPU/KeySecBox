#pragma once
#include <cstdint>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#  ifdef KSBOX_BUILD_DLL
#    define KSBOX_API __declspec(dllexport)
#  else
#    define KSBOX_API __declspec(dllimport)
#  endif
#else
#  define KSBOX_API
#endif

#pragma region 返回码

enum {
    KSBOX_OK = 0,
    KSBOX_ERR_WRONG_PASSWORD = 1,   // 密码错误（解密校验失败）
    KSBOX_ERR_NO_VAULT = 2,         // 未设置解锁密码
    KSBOX_ERR_NOT_UNLOCKED = 3,     // 未解锁
    KSBOX_ERR_IO = 4,               // 文件读写错误
    KSBOX_ERR_NOT_FOUND = 5,        // 分类/条目不存在
    KSBOX_ERR_DUP = 6,              // 名称重复
    KSBOX_ERR_GENERIC = -1
};

#pragma endregion

// 不透明存储库句柄
typedef struct ksbx_store ksbx_store;

#pragma region 生命周期

KSBOX_API ksbx_store* ksbx_store_create();
KSBOX_API void ksbx_store_destroy(ksbx_store* s);

#pragma endregion

#pragma region 初始化 / 解锁

// 多文件格式 (KSX3)，均位于 file 同目录：
//   <file>.settings  盐/KDF参数 + 密码校验块
//   <file>.index     分类+条目 meta（AES-GCM 整块加密，解锁即载入全部）
//   <file>.data      逐条独立加密的账号/密码/备注（追加写+墓碑）
//   <file>.tomb      墓碑（已删除 id）
//   <file>.recovery  恢复密钥（逐条 AES-GCM）
// 内置"未分类"(id=0)，setup 自动建立，不可删改。
KSBOX_API int ksbx_open(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd);
KSBOX_API int ksbx_setup(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd);
KSBOX_API int ksbx_change_password(ksbx_store* s, const wchar_t* newMasterPwd);
KSBOX_API int ksbx_verify_password(ksbx_store* s, const wchar_t* masterPwd);

#pragma endregion

#pragma region 分类

KSBOX_API long long ksbx_add_category(ksbx_store* s, const wchar_t* name); // 返回 id，<0 错误码
KSBOX_API int ksbx_rename_category(ksbx_store* s, long long id, const wchar_t* name);
KSBOX_API int ksbx_remove_category(ksbx_store* s, long long id); // 同时删除其下条目
KSBOX_API wchar_t* ksbx_list_categories(ksbx_store* s);          // JSON 数组，需 ksbx_free

#pragma endregion

#pragma region 条目

KSBOX_API long long ksbx_add_entry(ksbx_store* s, long long categoryId,
    const wchar_t* account, const wchar_t* password, const wchar_t* note); // 返回 id，<0 错误码
KSBOX_API int ksbx_update_entry(ksbx_store* s, long long id,
    long long categoryId, const wchar_t* account, const wchar_t* password, const wchar_t* note);
KSBOX_API int ksbx_remove_entry(ksbx_store* s, long long id);
KSBOX_API wchar_t* ksbx_get_entry(ksbx_store* s, long long id); // JSON 对象，需 ksbx_free

#pragma endregion

#pragma region 恢复密钥

// 设置条目恢复密钥全集（JSON 字符串数组，如 "[\"k1\"]"；空串/[] 删除记录）。
KSBOX_API int ksbx_set_recovery(ksbx_store* s, long long id, const wchar_t* keysJson);
KSBOX_API wchar_t* ksbx_get_recovery(ksbx_store* s, long long id); // JSON 数组，需 ksbx_free

#pragma endregion

#pragma region 查询

KSBOX_API wchar_t* ksbx_query_all(ksbx_store* s);               // 全部条目
KSBOX_API wchar_t* ksbx_query_category(ksbx_store* s, long long categoryId); // 该分类条目
KSBOX_API wchar_t* ksbx_search(ksbx_store* s, const wchar_t* keyword);       // 按账户/备注搜索

#pragma endregion

#pragma region 保存 / 墓碑 / 诊断

KSBOX_API int ksbx_save(ksbx_store* s); // 变更后加密写盘（所有写操作需显式 save）

// 墓碑上限（写入 settings；两者不可同时为 0）。超限会立即压缩。
KSBOX_API int ksbx_set_tomb_limit(ksbx_store* s, uint32_t maxBytes, uint32_t maxCount);
KSBOX_API int ksbx_get_tomb_limit(ksbx_store* s, uint32_t* outMaxBytes, uint32_t* outMaxCount);

// 诊断开关（写入 settings；开启后写操作追加到 <file>.diag.log，
// 仅记操作名/id/计数/返回码，不含机密）。
KSBOX_API int ksbx_get_diagnostics(ksbx_store* s, int* outEnabled);
KSBOX_API int ksbx_set_diagnostics(ksbx_store* s, int enabled);

#pragma endregion

// 释放本库分配的字符串
KSBOX_API void ksbx_free(void* ptr);

#ifdef __cplusplus
}
#endif
