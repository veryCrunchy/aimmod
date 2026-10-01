#include "AvatarImage.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstdlib>

namespace bridge::avatar
{
    namespace
    {
        constexpr std::array<std::uint16_t, 29> LengthBase = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
        constexpr std::array<std::uint8_t, 29> LengthExtra = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
        constexpr std::array<std::uint16_t, 30> DistBase = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
        constexpr std::array<std::uint8_t, 30> DistExtra = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
        constexpr std::size_t Window = 32768;
        constexpr int MinMatch = 3, MaxMatch = 258, MaxChain = 64;

        class Bits
        {
        public:
            explicit Bits(std::vector<std::uint8_t>& out) : m_out(out) {}
            // LSB first, as deflate packs extra bits and block headers.
            void Put(std::uint32_t value, int count)
            {
                m_buffer |= value << m_count;
                m_count += count;
                while (m_count >= 8)
                {
                    m_out.push_back(static_cast<std::uint8_t>(m_buffer & 0xFF));
                    m_buffer >>= 8;
                    m_count -= 8;
                }
            }
            // Huffman codes are defined MSB first.
            void Code(std::uint32_t code, int length)
            {
                std::uint32_t reversed = 0;
                for (int i = 0; i < length; ++i) reversed |= ((code >> i) & 1u) << (length - 1 - i);
                Put(reversed, length);
            }
            void Flush()
            {
                if (m_count > 0) m_out.push_back(static_cast<std::uint8_t>(m_buffer & 0xFF));
                m_buffer = 0;
                m_count = 0;
            }

        private:
            std::vector<std::uint8_t>& m_out;
            std::uint32_t m_buffer = 0;
            int m_count = 0;
        };

        void Symbol(Bits& bits, int symbol)
        {
            if (symbol < 144) bits.Code(0x30 + symbol, 8);
            else if (symbol < 256) bits.Code(0x190 + (symbol - 144), 9);
            else if (symbol < 280) bits.Code(symbol - 256, 7);
            else bits.Code(0xC0 + (symbol - 280), 8);
        }

        void Match(Bits& bits, int length, int distance)
        {
            int li = static_cast<int>(LengthBase.size()) - 1;
            while (LengthBase[li] > length) --li;
            Symbol(bits, 257 + li);
            if (LengthExtra[li]) bits.Put(static_cast<std::uint32_t>(length - LengthBase[li]), LengthExtra[li]);
            int di = static_cast<int>(DistBase.size()) - 1;
            while (DistBase[di] > distance) --di;
            bits.Code(static_cast<std::uint32_t>(di), 5);
            if (DistExtra[di]) bits.Put(static_cast<std::uint32_t>(distance - DistBase[di]), DistExtra[di]);
        }

        void Be32(std::vector<std::uint8_t>& out, std::uint32_t v)
        {
            out.push_back(static_cast<std::uint8_t>(v >> 24));
            out.push_back(static_cast<std::uint8_t>(v >> 16));
            out.push_back(static_cast<std::uint8_t>(v >> 8));
            out.push_back(static_cast<std::uint8_t>(v));
        }

        void Chunk(std::vector<std::uint8_t>& png, const char* type, const std::vector<std::uint8_t>& data)
        {
            Be32(png, static_cast<std::uint32_t>(data.size()));
            const std::size_t start = png.size();
            png.insert(png.end(), type, type + 4);
            png.insert(png.end(), data.begin(), data.end());
            Be32(png, Crc32(png.data() + start, png.size() - start));
        }

        int Paeth(int a, int b, int c)
        {
            const int p = a + b - c, pa = std::abs(p - a), pb = std::abs(p - b), pc = std::abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    } // namespace

    std::uint32_t Crc32(const std::uint8_t* data, std::size_t size, std::uint32_t crc)
    {
        static const auto table = [] {
            std::array<std::uint32_t, 256> t{};
            for (std::uint32_t n = 0; n < 256; ++n)
            {
                std::uint32_t c = n;
                for (int k = 0; k < 8; ++k) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }();
        crc = ~crc;
        for (std::size_t i = 0; i < size; ++i) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    std::uint32_t Adler32(const std::uint8_t* data, std::size_t size)
    {
        std::uint32_t a = 1, b = 0;
        for (std::size_t i = 0; i < size; ++i)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    std::vector<std::uint8_t> Zlib(const std::uint8_t* data, std::size_t size)
    {
        std::vector<std::uint8_t> out = {0x78, 0x01}; // 32 KiB window, fastest-level flag
        Bits bits(out);
        bits.Put(1, 1); // BFINAL
        bits.Put(1, 2); // BTYPE 01: fixed Huffman
        constexpr std::size_t HashSize = 1u << 15;
        std::vector<std::int32_t> head(HashSize, -1), prev(size, -1);
        auto hash = [&](std::size_t i) { return ((static_cast<std::uint32_t>(data[i]) << 10) ^ (static_cast<std::uint32_t>(data[i + 1]) << 5) ^ data[i + 2]) & (HashSize - 1); };
        auto insert = [&](std::size_t i) {
            if (i + MinMatch > size) return;
            const auto h = hash(i);
            prev[i] = head[h];
            head[h] = static_cast<std::int32_t>(i);
        };
        std::size_t i = 0;
        while (i < size)
        {
            int bestLength = 0, bestDistance = 0;
            if (i + MinMatch <= size)
            {
                const std::size_t limit = std::min<std::size_t>(MaxMatch, size - i);
                int chain = MaxChain;
                for (std::int32_t candidate = head[hash(i)]; candidate >= 0 && chain-- > 0; candidate = prev[candidate])
                {
                    const std::size_t distance = i - static_cast<std::size_t>(candidate);
                    if (distance == 0 || distance > Window) break;
                    std::size_t length = 0;
                    while (length < limit && data[candidate + length] == data[i + length]) ++length;
                    if (static_cast<int>(length) > bestLength)
                    {
                        bestLength = static_cast<int>(length);
                        bestDistance = static_cast<int>(distance);
                        if (length == limit) break;
                    }
                }
            }
            if (bestLength >= MinMatch)
            {
                Match(bits, bestLength, bestDistance);
                for (int k = 0; k < bestLength; ++k) insert(i + k);
                i += static_cast<std::size_t>(bestLength);
            }
            else
            {
                Symbol(bits, data[i]);
                insert(i);
                ++i;
            }
        }
        Symbol(bits, 256); // end of block
        bits.Flush();
        Be32(out, Adler32(data, size));
        return out;
    }

    std::string Hash(const std::uint8_t* rgba, std::uint32_t width, std::uint32_t height)
    {
        std::uint64_t h = 1469598103934665603ull;
        auto mix = [&](std::uint8_t b) { h = (h ^ b) * 1099511628211ull; };
        for (int s = 0; s < 32; s += 8) mix(static_cast<std::uint8_t>(width >> s));
        for (int s = 0; s < 32; s += 8) mix(static_cast<std::uint8_t>(height >> s));
        const std::size_t size = static_cast<std::size_t>(width) * height * 4;
        for (std::size_t i = 0; i < size; ++i) mix(rgba[i]);
        char buf[17];
        std::snprintf(buf, sizeof(buf), "%016llx", static_cast<unsigned long long>(h));
        return buf;
    }

    std::vector<std::uint8_t> EncodePng(const std::uint8_t* rgba, std::size_t size, std::uint32_t width, std::uint32_t height)
    {
        if (!rgba || width == 0 || height == 0 || width > MaxEdge || height > MaxEdge || size < static_cast<std::size_t>(width) * height * 4) return {};
        // Scanlines, each with the filter that leaves the smallest residuals.
        const std::size_t stride = static_cast<std::size_t>(width) * 4;
        std::vector<std::uint8_t> raw;
        raw.reserve((stride + 1) * height);
        std::vector<std::uint8_t> candidate(stride), best(stride);
        for (std::uint32_t y = 0; y < height; ++y)
        {
            const std::uint8_t* row = rgba + y * stride;
            const std::uint8_t* up = y ? rgba + (y - 1) * stride : nullptr;
            long bestCost = -1;
            std::uint8_t bestType = 0;
            for (std::uint8_t type = 0; type < 5; ++type)
            {
                long cost = 0;
                for (std::size_t x = 0; x < stride; ++x)
                {
                    const int a = x >= 4 ? row[x - 4] : 0, b = up ? up[x] : 0, c = up && x >= 4 ? up[x - 4] : 0;
                    int predicted = 0;
                    switch (type)
                    {
                    case 1: predicted = a; break;
                    case 2: predicted = b; break;
                    case 3: predicted = (a + b) / 2; break;
                    case 4: predicted = Paeth(a, b, c); break;
                    default: break;
                    }
                    const auto v = static_cast<std::uint8_t>(row[x] - predicted);
                    candidate[x] = v;
                    cost += v < 128 ? v : 256 - v;
                }
                if (bestCost < 0 || cost < bestCost)
                {
                    bestCost = cost;
                    bestType = type;
                    best.swap(candidate);
                }
            }
            raw.push_back(bestType);
            raw.insert(raw.end(), best.begin(), best.end());
        }
        std::vector<std::uint8_t> png = {0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n'};
        std::vector<std::uint8_t> ihdr;
        Be32(ihdr, width);
        Be32(ihdr, height);
        ihdr.insert(ihdr.end(), {8, 6, 0, 0, 0}); // 8-bit RGBA, deflate, adaptive filters, no interlace
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", Zlib(raw.data(), raw.size()));
        Chunk(png, "IEND", {});
        return png;
    }
} // namespace bridge::avatar
