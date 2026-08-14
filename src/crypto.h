#pragma once
#include <string>
#include <vector>
#include <cstdint>

namespace ksbx {
namespace crypto {

// PBKDF2-HMAC-SHA256 派生密钥（防暴力破解：高迭代次数）。
// 建议 iterations >= 600000（OWASP 2023 推荐值）
bool derive_key(const std::wstring& password, const std::vector<uint8_t>& salt,
                uint32_t iterations, std::vector<uint8_t>& out_key);

// 生成随机字节（底层 provider 缓存，避免每次调用重复创建）
void random_bytes(std::vector<uint8_t>& out, size_t n);

// AES-256-GCM 会话：缓存算法句柄 + 已导入密钥句柄，
// 同一密钥的多次加解密避免重复 Open/Import（加解密性能关键优化）。
struct GcmCtx {
    void* alg = nullptr;         // BCRYPT_ALG_HANDLE（在 cpp 内转换）
    void* key = nullptr;         // BCRYPT_KEY_HANDLE
    unsigned long objLen = 0;
    std::vector<uint8_t> obj;
    std::vector<uint8_t> keyBlob; // 已导入密钥的数据 blob（供重新导入）
    bool valid = false;

    GcmCtx() = default;
    GcmCtx(const GcmCtx&) = delete;
    GcmCtx& operator=(const GcmCtx&) = delete;
    ~GcmCtx() { free(); }

    // 用密钥初始化会话（打开 AES-GCM 算法 + 导入密钥）
    bool init(const std::vector<uint8_t>& keyBytes);
    void free();
    bool ok() const { return valid; }

    // 加密：输出 nonce(12) + ciphertext + tag(16)
    bool encrypt(const std::vector<uint8_t>& plaintext,
                 std::vector<uint8_t>& out_nonce, std::vector<uint8_t>& out_cipher);
    // 解密：cipher 含末尾 16 字节 tag。成功返回 true（tag 校验通过）
    bool decrypt(const std::vector<uint8_t>& nonce,
                 const std::vector<uint8_t>& cipher, std::vector<uint8_t>& out_plain);
};

} // namespace crypto
} // namespace ksbx
