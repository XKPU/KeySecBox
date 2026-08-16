#pragma once
#include <string>
#include <vector>
#include <cstdint>

namespace ksbx {
namespace crypto {

#pragma region 密钥与随机数

// PBKDF2-HMAC-SHA256 派生密钥（防暴力破解，建议 iterations >= 600000）
bool derive_key(const std::wstring& password, const std::vector<uint8_t>& salt,
                uint32_t iterations, std::vector<uint8_t>& out_key);

// 生成随机字节（缓存 provider）
void random_bytes(std::vector<uint8_t>& out, size_t n);

#pragma endregion

#pragma region AES-256-GCM

// 缓存算法句柄与密钥句柄，避免重复 Open/Import（性能关键）。
struct GcmCtx {
    void* alg = nullptr;         // BCRYPT_ALG_HANDLE
    void* key = nullptr;         // BCRYPT_KEY_HANDLE
    unsigned long objLen = 0;
    std::vector<uint8_t> obj;
    std::vector<uint8_t> keyBlob; // 已导入密钥 blob（供重新导入）
    bool valid = false;

    GcmCtx() = default;
    GcmCtx(const GcmCtx&) = delete;
    GcmCtx& operator=(const GcmCtx&) = delete;
    ~GcmCtx() { free(); }

    bool init(const std::vector<uint8_t>& keyBytes);
    void free();
    bool ok() const { return valid; }

    // 加密：输出 nonce(12) + ciphertext + tag(16)
    bool encrypt(const std::vector<uint8_t>& plaintext,
                 std::vector<uint8_t>& out_nonce, std::vector<uint8_t>& out_cipher);
    // 解密：cipher 末尾含 16 字节 tag；tag 校验通过返回 true
    bool decrypt(const std::vector<uint8_t>& nonce,
                 const std::vector<uint8_t>& cipher, std::vector<uint8_t>& out_plain);
};

#pragma endregion

} // namespace crypto
} // namespace ksbx
