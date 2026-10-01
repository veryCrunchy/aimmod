#pragma once
// Steam profile pictures for the service (feature "avatar"): the RGBA that
// ISteamUtils::GetImageRGBA returns, encoded as a small PNG (8-bit RGBA, one
// IDAT, zlib with fixed-Huffman deflate), plus a content hash the service
// caches by. Steam-independent so the tests can check it.

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace bridge::avatar
{
    // Largest edge the bridge sends (GetMediumFriendAvatar is 64x64).
    constexpr std::uint32_t MaxEdge = 64;
    // Largest PNG put on the pipe (base64 grows it by a third; the pipe frame is 64 KiB).
    constexpr std::size_t MaxPngBytes = 40 * 1024;

    // FNV-1a 64 over width, height and the pixels, as 16 lowercase hex digits.
    std::string Hash(const std::uint8_t* rgba, std::uint32_t width, std::uint32_t height);

    // Empty when the size is zero, above MaxEdge or the buffer is short.
    std::vector<std::uint8_t> EncodePng(const std::uint8_t* rgba, std::size_t size, std::uint32_t width, std::uint32_t height);

    // zlib stream (RFC 1950) around a fixed-Huffman deflate (RFC 1951) with LZ77 matches.
    std::vector<std::uint8_t> Zlib(const std::uint8_t* data, std::size_t size);

    std::uint32_t Crc32(const std::uint8_t* data, std::size_t size, std::uint32_t crc = 0);
    std::uint32_t Adler32(const std::uint8_t* data, std::size_t size);
} // namespace bridge::avatar
