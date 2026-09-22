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
    KSBOX_ERR_LEGACY = 7,           // 检测到旧版 1.0.x 库（需通过导入迁移）
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

// 版本验证。
// open 检测到旧版返回 KSBOX_ERR_LEGACY，应引导用户经导入迁移（ksbx_open_legacy）。
KSBOX_API int ksbx_open(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd);
KSBOX_API int ksbx_setup(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd);
KSBOX_API int ksbx_change_password(ksbx_store* s, const wchar_t* newMasterPwd);
KSBOX_API int ksbx_verify_password(ksbx_store* s, const wchar_t* masterPwd);

// 只读打开旧版库。
// 仅供导入合并使用：查询 API 可用，禁止 ksbx_save / 写操作。
KSBOX_API int ksbx_open_legacy(ksbx_store* s, const wchar_t* legacyDir, const wchar_t* masterPwd);

#pragma endregion

#pragma region 分类

KSBOX_API long long ksbx_add_category(ksbx_store* s, const wchar_t* name); // 返回 id，<0 错误码
KSBOX_API int ksbx_rename_category(ksbx_store* s, long long id, const wchar_t* name);
KSBOX_API int ksbx_move_category(ksbx_store* s, long long id, long long newPos); // 移动到 newPos；内置"未分类"恒在首位
KSBOX_API int ksbx_remove_category(ksbx_store* s, long long id); // 同时删除其下条目
KSBOX_API wchar_t* ksbx_list_categories(ksbx_store* s);          // JSON 数组，需 ksbx_free

#pragma endregion

#pragma region 条目

// categoryIdsJson：JSON 数字数组。
KSBOX_API long long ksbx_add_entry(ksbx_store* s, const wchar_t* categoryIdsJson,
    const wchar_t* account, const wchar_t* password, const wchar_t* note); // 返回 id，<0 错误码
KSBOX_API int ksbx_update_entry(ksbx_store* s, long long id,
    const wchar_t* categoryIdsJson, const wchar_t* account, const wchar_t* password, const wchar_t* note);
KSBOX_API int ksbx_remove_entry(ksbx_store* s, long long id);
KSBOX_API int ksbx_move_entry(ksbx_store* s, long long id, long long categoryId, long long newPos); // 在 categoryId 分类内移动到 newPos
KSBOX_API int ksbx_move_all_entry(ksbx_store* s, long long id, long long newPos); // 在"全部"视图内移动（独立隔离排序）
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

#pragma region 保存 / 诊断

KSBOX_API int ksbx_save(ksbx_store* s); // 变更后加密写盘（所有写操作需显式 save）

// 诊断开关.
KSBOX_API int ksbx_get_diagnostics(ksbx_store* s, int* outEnabled);
KSBOX_API int ksbx_set_diagnostics(ksbx_store* s, int enabled);

#pragma endregion

// 释放本库分配的字符串
KSBOX_API void ksbx_free(void* ptr);

#ifdef __cplusplus
}
#endif
