#include "crypto.h"

#define NOMINMAX
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <bcrypt.h>
#include <mutex>
#include <string>
#include <vector>
#include <cstdint>

#pragma comment(lib, "bcrypt.lib")

// BCryptOpenAlgorithmProvider / BCryptImportKey 开销大，
// GcmCtx 缓存算法句柄与密钥句柄，同密钥下只做一次。

namespace ksbx {
namespace crypto {

#pragma region 内部工具

namespace {

BCRYPT_ALG_HANDLE open_aes_gcm()
{
    BCRYPT_ALG_HANDLE h = nullptr;
    if (BCryptOpenAlgorithmProvider(&h, BCRYPT_AES_ALGORITHM, nullptr, 0) != 0) return nullptr;
    wchar_t mode[] = BCRYPT_CHAIN_MODE_GCM;
    if (BCryptSetProperty(h, BCRYPT_CHAINING_MODE,
            reinterpret_cast<PUCHAR>(mode), sizeof(mode), 0) != 0) {
        BCryptCloseAlgorithmProvider(h, 0);
        return nullptr;
    }
    return h;
}

} // namespace

#pragma endregion

#pragma region GcmCtx

bool GcmCtx::init(const std::vector<uint8_t>& keyBytes)
{
    free();
    if (keyBytes.size() != 32) return false;

    BCRYPT_ALG_HANDLE hAlg = open_aes_gcm();
    if (!hAlg) return false;
    alg = hAlg;

    DWORD cbData = 0;
    if (BCryptGetProperty(hAlg, BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&objLen), sizeof(objLen), &cbData, 0) != 0) {
        free();
        return false;
    }
    obj.assign(objLen, 0);

    keyBlob.clear();
    keyBlob.resize(sizeof(BCRYPT_KEY_DATA_BLOB_HEADER) + keyBytes.size());
    auto* hdr = reinterpret_cast<BCRYPT_KEY_DATA_BLOB_HEADER*>(keyBlob.data());
    hdr->dwMagic = BCRYPT_KEY_DATA_BLOB_MAGIC;
    hdr->dwVersion = BCRYPT_KEY_DATA_BLOB_VERSION1;
    hdr->cbKeyData = static_cast<ULONG>(keyBytes.size());
    memcpy(keyBlob.data() + sizeof(BCRYPT_KEY_DATA_BLOB_HEADER), keyBytes.data(), keyBytes.size());

    BCRYPT_KEY_HANDLE hKey = nullptr;
    if (BCryptImportKey(hAlg, nullptr, BCRYPT_KEY_DATA_BLOB, &hKey,
            obj.data(), objLen, keyBlob.data(), static_cast<ULONG>(keyBlob.size()), 0) != 0) {
        free();
        return false;
    }
    key = hKey;
    valid = true;
    return true;
}

void GcmCtx::free()
{
    if (key) { BCryptDestroyKey(reinterpret_cast<BCRYPT_KEY_HANDLE>(key)); key = nullptr; }
    if (alg) { BCryptCloseAlgorithmProvider(reinterpret_cast<BCRYPT_ALG_HANDLE>(alg), 0); alg = nullptr; }
    obj.clear();
    keyBlob.clear();
    objLen = 0;
    valid = false;
}

bool GcmCtx::encrypt(const std::vector<uint8_t>& plaintext,
                     std::vector<uint8_t>& out_nonce, std::vector<uint8_t>& out_cipher)
{
    if (!valid) return false;
    if (plaintext.empty()) return false;

    BCRYPT_KEY_HANDLE hKey = reinterpret_cast<BCRYPT_KEY_HANDLE>(key);

    out_nonce.assign(12, 0);
    random_bytes(out_nonce, 12);

    std::vector<uint8_t> tag(16, 0);

    BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
    BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
    authInfo.pbNonce = out_nonce.data();
    authInfo.cbNonce = static_cast<ULONG>(out_nonce.size());
    authInfo.pbTag = tag.data();
    authInfo.cbTag = static_cast<ULONG>(tag.size());

    ULONG cbCipher = 0;
    NTSTATUS st = BCryptEncrypt(hKey, const_cast<PUCHAR>(plaintext.data()),
        static_cast<ULONG>(plaintext.size()), &authInfo,
        nullptr, 0, nullptr, 0, &cbCipher, 0);
    if (st != 0 && st != STATUS_BUFFER_TOO_SMALL) return false;

    out_cipher.assign(cbCipher, 0);
    ULONG cbResult = 0;
    st = BCryptEncrypt(hKey, const_cast<PUCHAR>(plaintext.data()),
        static_cast<ULONG>(plaintext.size()), &authInfo,
        nullptr, 0, out_cipher.data(), static_cast<ULONG>(cbCipher), &cbResult, 0);
    if (st != 0) return false;

    out_cipher.insert(out_cipher.end(), tag.begin(), tag.end());
    return true;
}

bool GcmCtx::decrypt(const std::vector<uint8_t>& nonce,
                     const std::vector<uint8_t>& cipher, std::vector<uint8_t>& out_plain)
{
    if (!valid) return false;
    if (cipher.size() < 16) return false;

    BCRYPT_KEY_HANDLE hKey = reinterpret_cast<BCRYPT_KEY_HANDLE>(key);

    size_t ctLen = cipher.size() - 16;
    std::vector<uint8_t> ct(cipher.begin(), cipher.begin() + ctLen);
    std::vector<uint8_t> tag(cipher.begin() + ctLen, cipher.end());

    BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
    BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
    authInfo.pbNonce = const_cast<PUCHAR>(nonce.data());
    authInfo.cbNonce = static_cast<ULONG>(nonce.size());
    authInfo.pbTag = tag.data();
    authInfo.cbTag = static_cast<ULONG>(tag.size());

    ULONG cbPlain = 0;
    NTSTATUS st = BCryptDecrypt(hKey, ct.data(), static_cast<ULONG>(ct.size()),
        &authInfo, nullptr, 0, nullptr, 0, &cbPlain, 0);
    if (st != 0 && st != STATUS_BUFFER_TOO_SMALL) return false;

    out_plain.assign(cbPlain, 0);
    ULONG cbResult = 0;
    st = BCryptDecrypt(hKey, ct.data(), static_cast<ULONG>(ct.size()),
        &authInfo, nullptr, 0, out_plain.data(), static_cast<ULONG>(cbPlain), &cbResult, 0);
    return st == 0; // GCM tag 不匹配时返回 STATUS_AUTH_TAG_MISMATCH
}

#pragma endregion

#pragma region 密钥派生

bool derive_key(const std::wstring& password, const std::vector<uint8_t>& salt,
                uint32_t iterations, std::vector<uint8_t>& out_key)
{
    int pwlen = WideCharToMultiByte(CP_UTF8, 0, password.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (pwlen <= 0) return false;
    std::string pw(pwlen - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, password.c_str(), -1, &pw[0], pwlen, nullptr, nullptr);

    BCRYPT_ALG_HANDLE hAlg = nullptr;
    if (BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr,
            BCRYPT_ALG_HANDLE_HMAC_FLAG) != 0) return false;

    out_key.assign(32, 0);
    NTSTATUS st = BCryptDeriveKeyPBKDF2(
        hAlg,
        reinterpret_cast<PUCHAR>(const_cast<char*>(pw.data())),
        static_cast<ULONG>(pw.size()),
        const_cast<PUCHAR>(salt.data()),
        static_cast<ULONG>(salt.size()),
        static_cast<ULONGLONG>(iterations),
        out_key.data(),
        static_cast<ULONG>(out_key.size()),
        0);

    BCryptCloseAlgorithmProvider(hAlg, 0);
    return st == 0;
}

#pragma endregion

#pragma region 随机数

void random_bytes(std::vector<uint8_t>& out, size_t n)
{
    out.resize(n, 0);
    // 惰性缓存 RNG provider
    static BCRYPT_ALG_HANDLE s_rand = nullptr;
    static std::once_flag s_flag;
    std::call_once(s_flag, [] {
        BCryptOpenAlgorithmProvider(&s_rand, BCRYPT_RNG_ALGORITHM, nullptr, 0);
    });
    if (s_rand) {
        BCryptGenRandom(s_rand, out.data(), static_cast<ULONG>(n), 0);
    }
}

#pragma endregion

} // namespace crypto
} // namespace ksbx
