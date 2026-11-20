// SPDX-FileCopyrightText: 2026 K_PU
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 核心 C API 测试：只经由 keysecbox.h 调用，不触碰内部实现。
// 覆盖解锁/锁定、分类与条目的增删改查、多分类、"全部"视图排序、
// 恢复密钥、改密、明文落盘以外的持久化与诊断开关。
//
// 每个用例在独立临时目录中建库，结束时整体删除。

#include "keysecbox.h"

#include <windows.h>

#include <cstdio>
#include <exception>
#include <filesystem>
#include <string>
#include <system_error>
#include <vector>

namespace fs = std::filesystem;

namespace {

int g_checks = 0;
int g_failures = 0;
const char* g_current = "";

void report_fail(int line, const std::string& what)
{
    ++g_failures;
    std::printf("  FAIL [%s:%d] %s\n", g_current, line, what.c_str());
}

#define CHECK(cond)                                                       \
    do {                                                                  \
        ++g_checks;                                                       \
        if (!(cond)) report_fail(__LINE__, #cond);                        \
    } while (0)

#define CHECK_RC(expr, expected)                                          \
    do {                                                                  \
        long long _actual = (long long)(expr);                            \
        ++g_checks;                                                       \
        if (_actual != (long long)(expected))                             \
            report_fail(__LINE__, std::string(#expr) + " = "              \
                + std::to_string(_actual) + ", expected "                 \
                + std::to_string((long long)(expected)));                 \
    } while (0)

// 取走 C API 分配的字符串（必须与 ksbx_free 配对，否则泄漏）
std::wstring take(wchar_t* p)
{
    if (!p) return L"";
    std::wstring s(p);
    ksbx_free(p);
    return s;
}

bool contains(const std::wstring& hay, const std::wstring& needle)
{
    return hay.find(needle) != std::wstring::npos;
}

size_t pos_of(const std::wstring& hay, const std::wstring& needle)
{
    return hay.find(needle);
}

// 条目在 JSON 中的唯一出现位置：避免用裸数字比对（"1" 会命中 "12"）
std::wstring id_token(long long id)
{
    return L"\"id\":" + std::to_wstring(id) + L",";
}

// 记录流里的条目数量：entries 查询结果中每个条目都有一个 "id":
int count_ids(const std::wstring& json)
{
    int n = 0;
    size_t p = 0;
    while ((p = json.find(L"\"id\":", p)) != std::wstring::npos) { ++n; ++p; }
    return n;
}

struct TempVault {
    fs::path dir;
    std::wstring base;

    // 测试数据放在 exe 同目录下的 ksbox_test_data：TEMP 环境变量缺失时
    //（如从 Git Bash 启动）fs::temp_directory_path 会退化到不可写的 Windows 目录。
    static fs::path test_root()
    {
        wchar_t buf[MAX_PATH] = { 0 };
        GetModuleFileNameW(nullptr, buf, MAX_PATH);
        fs::path dir = fs::path(buf).parent_path() / L"ksbox_test_data";
        std::error_code ec;
        fs::create_directories(dir, ec);
        return dir;
    }

    explicit TempVault(const char* tag)
    {
        static int seq = 0;
        dir = test_root()
            / ("ksbox_" + std::string(tag) + "_"
               + std::to_string(GetCurrentProcessId()) + "_" + std::to_string(++seq));
        std::error_code ec;
        fs::remove_all(dir, ec);
        fs::create_directories(dir, ec);
        if (ec)
            std::printf("  (warning) cannot create %ls: %s\n", dir.c_str(), ec.message().c_str());
        base = (dir / L"vault").wstring();
    }

    ~TempVault()
    {
        std::error_code ec;
        fs::remove_all(dir, ec);
    }

    std::wstring file(const wchar_t* ext) const { return base + ext; }
    bool has(const wchar_t* ext) const { return fs::exists(file(ext)); }
};

struct Store {
    ksbx_store* s;
    Store() : s(ksbx_store_create()) {}
    ~Store() { ksbx_store_destroy(s); }
    operator ksbx_store*() const { return s; }
};

void write_bytes(const std::wstring& path, const char* data, size_t len)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"wb") != 0 || !f) return;
    fwrite(data, 1, len, f);
    fclose(f);
}

#pragma region 用例

void test_lifecycle()
{
    TempVault v("lifecycle");

    Store st;
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"pw"), KSBOX_ERR_NO_VAULT);
    CHECK_RC(ksbx_save(st), KSBOX_ERR_NOT_UNLOCKED);
    CHECK_RC(ksbx_lock(st), KSBOX_ERR_NOT_UNLOCKED);

    // 旧版 1.0.x 标记：open 提示走导入而不是误建新库
    const char legacy[] = "KSX3 legacy placeholder ................................";
    write_bytes(v.file(L".settings"), legacy, sizeof(legacy) - 1);
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"pw"), KSBOX_ERR_LEGACY);
    fs::remove(v.file(L".settings"));

    // 空主密码不允许建库；建库后重复建库被拒绝
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L""), KSBOX_ERR_GENERIC);
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_ERR_GENERIC);
    CHECK(v.has(L".master"));
    CHECK(v.has(L".cats"));
    CHECK(v.has(L".map"));
    CHECK(v.has(L".entries"));
    CHECK(v.has(L".prefs"));

    // 旧版目录不存在时返回 NO_VAULT
    Store legacyStore;
    CHECK_RC(ksbx_open_legacy(legacyStore, (v.dir / L"nope").c_str(), L"pw"), KSBOX_ERR_NO_VAULT);
}

void test_unlock_and_lock()
{
    TempVault v("lock");
    {
        Store st;
        CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);
        long long id = ksbx_add_entry(st, L"[0]", L"alice", L"secret", L"note");
        CHECK(id > 0);
        CHECK_RC(ksbx_save(st), KSBOX_OK);
    }

    Store st;
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"wrong"), KSBOX_ERR_WRONG_PASSWORD);
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"master"), KSBOX_OK);
    CHECK_RC(ksbx_verify_password(st, L"master"), KSBOX_OK);
    CHECK_RC(ksbx_verify_password(st, L"wrong"), KSBOX_ERR_WRONG_PASSWORD);

    std::wstring all = take(ksbx_query_all(st));
    CHECK(count_ids(all) == 1);
    CHECK(contains(all, L"\"account\":\"alice\""));

    // 锁定后：所有查询与写入都不可用，句柄仍可重新解锁
    CHECK_RC(ksbx_lock(st), KSBOX_OK);
    CHECK(ksbx_query_all(st) == nullptr);
    CHECK(ksbx_list_categories(st) == nullptr);
    CHECK(ksbx_get_entry(st, 1) == nullptr);
    CHECK_RC(ksbx_save(st), KSBOX_ERR_NOT_UNLOCKED);
    CHECK_RC(ksbx_lock(st), KSBOX_ERR_NOT_UNLOCKED);

    CHECK_RC(ksbx_open(st, v.base.c_str(), L"master"), KSBOX_OK);
    all = take(ksbx_query_all(st));
    CHECK(count_ids(all) == 1);
    CHECK(contains(take(ksbx_get_entry(st, 1)), L"\"password\":\"secret\""));
}

void test_categories()
{
    TempVault v("cats");
    Store st;
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);

    // 内置"未分类"id=0，且恒居首位
    std::wstring cats = take(ksbx_list_categories(st));
    CHECK(contains(cats, L"{\"id\":0,\"name\":\"未分类\"}"));

    long long work = ksbx_add_category(st, L"工作");
    long long life = ksbx_add_category(st, L"生活");
    CHECK(work > 0);
    CHECK(life > 0);
    CHECK_RC(ksbx_add_category(st, L"工作"), KSBOX_ERR_DUP);
    CHECK_RC(ksbx_add_category(st, L"未分类"), KSBOX_ERR_DUP);

    cats = take(ksbx_list_categories(st));
    CHECK(pos_of(cats, L"未分类") < pos_of(cats, L"工作"));
    CHECK(pos_of(cats, L"工作") < pos_of(cats, L"生活"));

    // 内置分类不可移动 / 改名 / 删除
    CHECK_RC(ksbx_move_category(st, 0, 2), KSBOX_ERR_GENERIC);
    CHECK_RC(ksbx_rename_category(st, 0, L"改名"), KSBOX_ERR_GENERIC);
    CHECK_RC(ksbx_remove_category(st, 0), KSBOX_ERR_GENERIC);

    // 移动分类：位置 1 紧随"未分类"
    CHECK_RC(ksbx_move_category(st, life, 1), KSBOX_OK);
    cats = take(ksbx_list_categories(st));
    CHECK(pos_of(cats, L"生活") < pos_of(cats, L"工作"));

    CHECK_RC(ksbx_rename_category(st, work, L"工作台"), KSBOX_OK);
    cats = take(ksbx_list_categories(st));
    CHECK(contains(cats, L"\"name\":\"工作台\""));

    CHECK_RC(ksbx_remove_category(st, life), KSBOX_OK);
    cats = take(ksbx_list_categories(st));
    CHECK(!contains(cats, L"生活"));
    CHECK_RC(ksbx_remove_category(st, 12345), KSBOX_ERR_NOT_FOUND);
    CHECK_RC(ksbx_rename_category(st, 12345, L"x"), KSBOX_ERR_NOT_FOUND);
}

void test_entries()
{
    TempVault v("entries");
    Store st;
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);

    long long a = ksbx_add_category(st, L"A");
    long long b = ksbx_add_category(st, L"B");
    CHECK(a > 0 && b > 0);

    std::wstring catsA = L"[" + std::to_wstring(a) + L"]";
    std::wstring catsAB = L"[" + std::to_wstring(a) + L"," + std::to_wstring(b) + L"]";

    long long id1 = ksbx_add_entry(st, catsA.c_str(), L"alice", L"p1", L"n1");
    long long id2 = ksbx_add_entry(st, L"[999]", L"bob", L"p2", L"n2"); // 非法分类 → 未分类
    CHECK(id1 > 0 && id2 > 0);

    CHECK(count_ids(take(ksbx_query_category(st, a))) == 1);
    CHECK(count_ids(take(ksbx_query_category(st, 0))) == 1);
    CHECK(count_ids(take(ksbx_query_all(st))) == 2);

    // get_entry 是唯一返回密码的入口
    std::wstring e1 = take(ksbx_get_entry(st, id1));
    CHECK(contains(e1, L"\"account\":\"alice\""));
    CHECK(contains(e1, L"\"password\":\"p1\""));
    CHECK(contains(e1, L"\"note\":\"n1\""));

    // 更新为多分类：两个分类各出现一次，"全部"里仍只有一条
    CHECK_RC(ksbx_update_entry(st, id1, catsAB.c_str(), L"alice2", L"p2", L"n2"), KSBOX_OK);
    CHECK(count_ids(take(ksbx_query_category(st, a))) == 1);
    CHECK(count_ids(take(ksbx_query_category(st, b))) == 1);
    CHECK(count_ids(take(ksbx_query_all(st))) == 2);
    e1 = take(ksbx_get_entry(st, id1));
    CHECK(contains(e1, L"\"account\":\"alice2\""));
    CHECK(contains(e1, L"\"password\":\"p2\""));

    // 删除分类会连带删除只属于它的条目，多分类条目仅解除该分类
    CHECK_RC(ksbx_remove_category(st, b), KSBOX_OK);
    CHECK(count_ids(take(ksbx_query_all(st))) == 2);
    CHECK(contains(take(ksbx_get_entry(st, id1)), L"alice2"));

    CHECK_RC(ksbx_remove_entry(st, id2), KSBOX_OK);
    CHECK_RC(ksbx_remove_entry(st, id2), KSBOX_ERR_NOT_FOUND);
    CHECK(count_ids(take(ksbx_query_all(st))) == 1);

    // 分类内排序
    long long id3 = ksbx_add_entry(st, catsA.c_str(), L"carol", L"p3", L"n3");
    CHECK(count_ids(take(ksbx_query_category(st, a))) == 2);
    std::wstring catJson = take(ksbx_query_category(st, a));
    CHECK(pos_of(catJson, id_token(id1)) < pos_of(catJson, id_token(id3)));
    CHECK_RC(ksbx_move_entry(st, id3, a, 0), KSBOX_OK);
    catJson = take(ksbx_query_category(st, a));
    CHECK(pos_of(catJson, id_token(id3)) < pos_of(catJson, id_token(id1)));
    CHECK_RC(ksbx_move_entry(st, id3, 4242, 0), KSBOX_ERR_NOT_FOUND);
}

void test_all_view_pins()
{
    TempVault v("pins");
    Store st;
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);

    long long a = ksbx_add_category(st, L"A");
    long long b = ksbx_add_category(st, L"B");
    std::wstring catsA = L"[" + std::to_wstring(a) + L"]";
    std::wstring catsB = L"[" + std::to_wstring(b) + L"]";

    long long e1 = ksbx_add_entry(st, catsA.c_str(), L"e1", L"p", L"");
    long long e2 = ksbx_add_entry(st, catsA.c_str(), L"e2", L"p", L"");
    long long e3 = ksbx_add_entry(st, catsB.c_str(), L"e3", L"p", L"");

    // 默认顺序 = 分类序（A 后 B）+ 分类内序
    std::wstring all = take(ksbx_query_all(st));
    CHECK(pos_of(all, id_token(e1)) < pos_of(all, id_token(e2)));
    CHECK(pos_of(all, id_token(e2)) < pos_of(all, id_token(e3)));

    // "全部"视图内移动只改该条目的 pin，其余仍按默认序
    CHECK_RC(ksbx_move_all_entry(st, e3, 0), KSBOX_OK);
    all = take(ksbx_query_all(st));
    CHECK(pos_of(all, id_token(e3)) < pos_of(all, id_token(e1)));

    // pin 随保存/重开保留
    CHECK_RC(ksbx_save(st), KSBOX_OK);
    CHECK_RC(ksbx_lock(st), KSBOX_OK);
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"master"), KSBOX_OK);
    all = take(ksbx_query_all(st));
    CHECK(pos_of(all, id_token(e3)) < pos_of(all, id_token(e1)));

    // 删除条目会清掉它的 pin，不影响其他条目
    CHECK_RC(ksbx_remove_entry(st, e3), KSBOX_OK);
    CHECK_RC(ksbx_save(st), KSBOX_OK);
    all = take(ksbx_query_all(st));
    CHECK(count_ids(all) == 2);
}

void test_search_and_unicode()
{
    TempVault v("search");
    Store st;
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);

    const wchar_t* pwd = L"密 码🅿🔐";
    long long id1 = ksbx_add_entry(st, L"[0]", L"Alice@Example.COM", pwd, L"工作邮箱");
    ksbx_add_entry(st, L"[0]", L"bob", L"pw", L"银行密码");

    CHECK(count_ids(take(ksbx_search(st, L"alice"))) == 1);   // 大小写不敏感
    CHECK(count_ids(take(ksbx_search(st, L"EXAMPLE"))) == 1);
    CHECK(count_ids(take(ksbx_search(st, L"银行"))) == 1);
    CHECK(count_ids(take(ksbx_search(st, L"zzz"))) == 0);
    CHECK(count_ids(take(ksbx_search(st, L""))) == 2);        // 空关键字 = 全部

    // 非 ASCII 与代理对（emoji）经加密、落盘、重开后完整往返
    CHECK_RC(ksbx_save(st), KSBOX_OK);
    CHECK_RC(ksbx_lock(st), KSBOX_OK);
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"master"), KSBOX_OK);
    CHECK(contains(take(ksbx_get_entry(st, id1)), pwd));

    // 搜索只匹配账号与备注，不匹配密码
    CHECK(count_ids(take(ksbx_search(st, pwd))) == 0);
}

void test_recovery_keys()
{
    TempVault v("recovery");
    Store st;
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);

    long long id = ksbx_add_entry(st, L"[0]", L"acct", L"pw", L"");
    CHECK(ksbx_get_recovery(st, id) == nullptr); // 未设置

    CHECK_RC(ksbx_set_recovery(st, id, L"[\"k1\",\"含\\\"引号\\\"的密钥\",\"café\"]"), KSBOX_OK);
    std::wstring rec = take(ksbx_get_recovery(st, id));
    CHECK(contains(rec, L"\"k1\""));
    CHECK(contains(rec, L"café"));

    CHECK_RC(ksbx_save(st), KSBOX_OK);
    CHECK(v.has(L".recovery"));

    // 落盘后重新解密（走 recovery 文件而不是内存缓存）
    CHECK_RC(ksbx_lock(st), KSBOX_OK);
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"master"), KSBOX_OK);
    rec = take(ksbx_get_recovery(st, id));
    CHECK(contains(rec, L"\"k1\""));
    CHECK(contains(rec, L"含"));

    // 空数组 = 删除记录
    CHECK_RC(ksbx_set_recovery(st, id, L"[]"), KSBOX_OK);
    CHECK_RC(ksbx_save(st), KSBOX_OK);
    CHECK(ksbx_get_recovery(st, id) == nullptr);

    CHECK_RC(ksbx_set_recovery(st, 9999, L"[\"k\"]"), KSBOX_ERR_NOT_FOUND);
}

void test_change_password()
{
    TempVault v("changepw");
    Store st;
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"old"), KSBOX_OK);

    long long a = ksbx_add_category(st, L"分类");
    std::wstring cats = L"[" + std::to_wstring(a) + L"]";
    long long id = ksbx_add_entry(st, cats.c_str(), L"acct", L"秘密密码", L"备注");
    CHECK_RC(ksbx_set_recovery(st, id, L"[\"rk\"]"), KSBOX_OK);
    CHECK_RC(ksbx_save(st), KSBOX_OK);

    CHECK_RC(ksbx_change_password(st, L"new"), KSBOX_OK);
    CHECK_RC(ksbx_verify_password(st, L"new"), KSBOX_OK);
    CHECK_RC(ksbx_verify_password(st, L"old"), KSBOX_ERR_WRONG_PASSWORD);

    // 条目与恢复密钥都用新密钥重加密，可继续解密
    CHECK(contains(take(ksbx_get_entry(st, id)), L"秘密密码"));
    CHECK(contains(take(ksbx_get_recovery(st, id)), L"rk"));

    Store fresh;
    CHECK_RC(ksbx_open(fresh, v.base.c_str(), L"old"), KSBOX_ERR_WRONG_PASSWORD);
    CHECK_RC(ksbx_open(fresh, v.base.c_str(), L"new"), KSBOX_OK);
    CHECK(contains(take(ksbx_get_entry(fresh, id)), L"秘密密码"));
    CHECK(contains(take(ksbx_list_categories(fresh)), L"分类"));
}

void test_diagnostics_switch()
{
    TempVault v("diag");
    {
        Store st;
        CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);

        int enabled = -1;
        CHECK_RC(ksbx_get_diagnostics(st, &enabled), KSBOX_OK);
        CHECK(enabled == 0);

        CHECK_RC(ksbx_set_diagnostics(st, 1), KSBOX_OK);
        CHECK_RC(ksbx_get_diagnostics(st, &enabled), KSBOX_OK);
        CHECK(enabled == 1);

        ksbx_add_category(st, L"记录"); // 触发一次诊断写入
        CHECK(v.has(L".diag.log"));
    }

    // 开关随 .prefs 落盘
    Store st;
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"master"), KSBOX_OK);
    int enabled = -1;
    CHECK_RC(ksbx_get_diagnostics(st, &enabled), KSBOX_OK);
    CHECK(enabled == 1);
    CHECK_RC(ksbx_set_diagnostics(st, 0), KSBOX_OK);
}

void test_hidden_items()
{
    TempVault v("hidden");
    Store st;
    CHECK_RC(ksbx_setup(st, v.base.c_str(), L"master"), KSBOX_OK);

    const wchar_t* p1 = L"C:\\Temp\\账单-2026.txt";
    const wchar_t* p2 = L"D:\\Archive\\发票";

    CHECK_RC(ksbx_hidden_add(st, p1, L"年度账单"), KSBOX_OK);
    CHECK_RC(ksbx_hidden_add(st, p2, L"发票目录"), KSBOX_OK);
    CHECK_RC(ksbx_hidden_add(st, p1, L"重复"), KSBOX_ERR_DUP);
    CHECK_RC(ksbx_hidden_add(st, L"c:\\temp\\账单-2026.TXT", L"大小写不同"), KSBOX_ERR_DUP);
    CHECK_RC(ksbx_hidden_add(st, L"", L"空路径"), KSBOX_ERR_GENERIC);

    std::wstring list = take(ksbx_hidden_list(st));
    CHECK(contains(list, L"\"name\":\"年度账单\""));
    CHECK(contains(list, L"\"name\":\"发票目录\""));
    CHECK(contains(list, L"\"hasPassword\":false"));

    // 展示名可改，路径不变
    CHECK_RC(ksbx_hidden_rename(st, p1, L"改名后"), KSBOX_OK);
    CHECK(contains(take(ksbx_hidden_list(st)), L"\"name\":\"改名后\""));
    CHECK_RC(ksbx_hidden_rename(st, L"C:\\nope", L"x"), KSBOX_ERR_NOT_FOUND);

    // 未设置单独密码时校验一律通过
    CHECK_RC(ksbx_hidden_check_password(st, p1, L""), KSBOX_OK);
    CHECK_RC(ksbx_hidden_check_password(st, p1, L"任意"), KSBOX_OK);

    // 设置单独密码：首次无需旧密码，之后必须提供
    CHECK_RC(ksbx_hidden_set_password(st, p1, L"", L"item-pw"), KSBOX_OK);
    CHECK(contains(take(ksbx_hidden_list(st)), L"\"hasPassword\":true"));
    CHECK_RC(ksbx_hidden_check_password(st, p1, L"item-pw"), KSBOX_OK);
    CHECK_RC(ksbx_hidden_check_password(st, p1, L"wrong"), KSBOX_ERR_WRONG_PASSWORD);
    CHECK_RC(ksbx_hidden_check_password(st, p1, L""), KSBOX_ERR_WRONG_PASSWORD);
    CHECK_RC(ksbx_hidden_set_password(st, p1, L"wrong", L"other"), KSBOX_ERR_WRONG_PASSWORD);
    CHECK_RC(ksbx_hidden_set_password(st, p1, L"item-pw", L"item-pw2"), KSBOX_OK);
    CHECK_RC(ksbx_hidden_check_password(st, p1, L"item-pw2"), KSBOX_OK);
    CHECK_RC(ksbx_hidden_check_password(st, p1, L"item-pw"), KSBOX_ERR_WRONG_PASSWORD);

    // 落盘后重新打开：记录、展示名与单独密码都还在
    CHECK_RC(ksbx_save(st), KSBOX_OK);
    CHECK(v.has(L".hidden"));
    CHECK_RC(ksbx_lock(st), KSBOX_OK);
    CHECK_RC(ksbx_open(st, v.base.c_str(), L"master"), KSBOX_OK);
    list = take(ksbx_hidden_list(st));
    CHECK(contains(list, L"\"name\":\"改名后\""));
    CHECK(contains(list, L"\"name\":\"发票目录\""));
    CHECK_RC(ksbx_hidden_check_password(st, p1, L"item-pw2"), KSBOX_OK);

    // 改密后隐藏文件随主密钥重加密，单独密码不受影响
    CHECK_RC(ksbx_change_password(st, L"new-master"), KSBOX_OK);
    Store fresh;
    CHECK_RC(ksbx_open(fresh, v.base.c_str(), L"new-master"), KSBOX_OK);
    CHECK(contains(take(ksbx_hidden_list(fresh)), L"\"name\":\"改名后\""));
    CHECK_RC(ksbx_hidden_check_password(fresh, p1, L"item-pw2"), KSBOX_OK);

    // 清除单独密码后恢复为无需校验
    CHECK_RC(ksbx_hidden_set_password(fresh, p1, L"item-pw2", L""), KSBOX_OK);
    CHECK(contains(take(ksbx_hidden_list(fresh)), L"\"hasPassword\":false"));
    CHECK_RC(ksbx_hidden_check_password(fresh, p1, L"whatever"), KSBOX_OK);

    // 移除：大小写不敏感，重复移除报未找到
    CHECK_RC(ksbx_hidden_remove(fresh, L"D:\\archive\\发票"), KSBOX_OK);
    list = take(ksbx_hidden_list(fresh));
    CHECK(!contains(list, L"\"name\":\"发票目录\""));
    CHECK_RC(ksbx_hidden_remove(fresh, p2), KSBOX_ERR_NOT_FOUND);
    CHECK_RC(ksbx_hidden_check_password(fresh, p2, L""), KSBOX_ERR_NOT_FOUND);
}

#pragma endregion

struct TestCase {
    const char* name;
    void (*fn)();
};

} // namespace

int main()
{
    const TestCase cases[] = {
        { "lifecycle", test_lifecycle },
        { "unlock_and_lock", test_unlock_and_lock },
        { "categories", test_categories },
        { "entries", test_entries },
        { "all_view_pins", test_all_view_pins },
        { "search_and_unicode", test_search_and_unicode },
        { "recovery_keys", test_recovery_keys },
        { "hidden_items", test_hidden_items },
        { "change_password", test_change_password },
        { "diagnostics_switch", test_diagnostics_switch },
    };

    for (const auto& c : cases) {
        int before = g_failures;
        g_current = c.name;
        std::printf("[ RUN  ] %s\n", c.name);
        try {
            c.fn();
        } catch (const std::exception& ex) {
            report_fail(__LINE__, std::string("uncaught exception: ") + ex.what());
        }
        std::printf("[ %s ] %s\n", g_failures == before ? " OK " : "FAIL", c.name);
    }

    std::printf("\n%d checks, %d failure(s)\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
