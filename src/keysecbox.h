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

// 返回码
enum {
    KSBOX_OK = 0,
    KSBOX_ERR_WRONG_PASSWORD = 1,   // 解密证书校验失败 -> 密码错误
    KSBOX_ERR_NO_VAULT = 2,         // 尚未设置解锁密码
    KSBOX_ERR_NOT_UNLOCKED = 3,     // 尚未解锁
    KSBOX_ERR_IO = 4,               // 文件读写错误
    KSBOX_ERR_NOT_FOUND = 5,        // 分类/条目不存在
    KSBOX_ERR_DUP = 6,              // 名称重复
    KSBOX_ERR_GENERIC = -1
};

// 不透明存储库句柄
typedef struct ksbx_store ksbx_store;

// ---- 生命周期 ----
KSBOX_API ksbx_store* ksbx_store_create();
KSBOX_API void ksbx_store_destroy(ksbx_store* s);

// ---- 初始化 / 解锁 ----
// 文件格式 (magic "KSX3") 多文件，均位于 file 同目录：
//   <file>.settings  派生参数(salt/iter) + 校验块(密码核对)
//   <file>.index     分类树 + 条目 meta 数组（整体 AES-GCM 加密，解锁仅解此文件即可列全部）
//   <file>.data      逐条独立加密的密码/备注（追加写 + 墓碑，支持增量保存）
// 每条目分离为 meta(账户/分类，常驻内存索引) 与 secret(密码/备注，仅查看/编辑时解密)，
// 内存中永不常驻密码明文，最大限度降低内存泄露风险。
// 内置"未分类"分类(id=0)，setup 自动建立，不可删除/重命名。
// 尝试打开 file：若 .settings 不存在 -> KSBOX_ERR_NO_VAULT（需先 setup）
// 用 masterPwd 派生密钥解密校验块，失败返回 KSBOX_ERR_WRONG_PASSWORD
KSBOX_API int ksbx_open(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd);

// 首次设置解锁密码：生成 salt+iterations，派生密钥并加密空库保存。
KSBOX_API int ksbx_setup(ksbx_store* s, const wchar_t* file, const wchar_t* masterPwd);

// 已解锁后修改解锁密码（用新密码重新加密保存）。
KSBOX_API int ksbx_change_password(ksbx_store* s, const wchar_t* newMasterPwd);

// ---- 分类 ----
KSBOX_API long long ksbx_add_category(ksbx_store* s, const wchar_t* name); // 返回 id，<0 为错误码
KSBOX_API int ksbx_rename_category(ksbx_store* s, long long id, const wchar_t* name);
KSBOX_API int ksbx_remove_category(ksbx_store* s, long long id); // 同时移除其下条目
KSBOX_API wchar_t* ksbx_list_categories(ksbx_store* s);          // JSON 数组，需 ksbx_free

// ---- 条目 ----
// 返回新条目 id，<0 为错误码
KSBOX_API long long ksbx_add_entry(ksbx_store* s, long long categoryId,
    const wchar_t* account, const wchar_t* password, const wchar_t* note);
KSBOX_API int ksbx_update_entry(ksbx_store* s, long long id,
    long long categoryId, const wchar_t* account, const wchar_t* password, const wchar_t* note);
KSBOX_API int ksbx_remove_entry(ksbx_store* s, long long id);
KSBOX_API wchar_t* ksbx_get_entry(ksbx_store* s, long long id); // JSON 对象，需 ksbx_free

// ---- 双重验证恢复密钥（独立 <file>.recovery 文件，逐条加密）----
// 设置某条目的恢复密钥全集（JSON 字符串数组，如 "[\"k1\",\"k2\"]"；空串/[] = 删除该条恢复记录）。
// 支持逐把增删：调用方传修改后的全集。返回 KSBOX_OK/错误码。
KSBOX_API int ksbx_set_recovery(ksbx_store* s, long long id, const wchar_t* keysJson);
// 获取某条目恢复密钥（JSON 字符串数组），无记录/失败返回 NULL，需 ksbx_free。
KSBOX_API wchar_t* ksbx_get_recovery(ksbx_store* s, long long id);

// ---- 查询（高性能：分类走索引，搜索走遍历）----
KSBOX_API wchar_t* ksbx_query_all(ksbx_store* s);       // 全部条目 JSON 数组
KSBOX_API wchar_t* ksbx_query_category(ksbx_store* s, long long categoryId); // 该分类条目
KSBOX_API wchar_t* ksbx_search(ksbx_store* s, const wchar_t* keyword);        // 账户/备注 包含 keyword

// 变更后加密写盘（open/setup 成功后所有写操作需显式 save）
KSBOX_API int ksbx_save(ksbx_store* s);

// 墓碑上限设置（写回 settings 扩展区；两者不可同时为 0）。set 后若已超限会立即压缩一次。
KSBOX_API int ksbx_set_tomb_limit(ksbx_store* s, uint32_t maxBytes, uint32_t maxCount);
// 读取墓碑上限（maxBytes=0 表示不按大小限制；maxCount=0 表示不按条数限制）
KSBOX_API int ksbx_get_tomb_limit(ksbx_store* s, uint32_t* outMaxBytes, uint32_t* outMaxCount);

// 释放本库分配的字符串
KSBOX_API void ksbx_free(void* ptr);

#ifdef __cplusplus
}
#endif
