#include <aimmod/CosmeticsPreview.hpp>

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace aimmod
{
    namespace
    {
        bool IsName(std::string_view s, std::size_t max)
        {
            if (s.empty() || s.size() > max) return false;
            const auto alpha = [](char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); };
            if (!alpha(s[0])) return false;
            for (char c : s)
                if (!alpha(c) && !(c >= '0' && c <= '9') && c != '_') return false;
            return true;
        }
        bool Number(std::string_view s, double& out)
        {
            if (s.empty() || s.size() > 32) return false;
            const auto* end = s.data() + s.size();
            auto [ptr, ec] = std::from_chars(s.data(), end, out);
            return ec == std::errc{} && ptr == end && std::isfinite(out);
        }
        bool Integer(std::string_view s, std::int64_t& out)
        {
            if (s.empty() || s.size() > 20) return false;
            const auto* end = s.data() + s.size();
            auto [ptr, ec] = std::from_chars(s.data(), end, out);
            return ec == std::errc{} && ptr == end;
        }
        // "<Name>:<n>[,<n>...]" with exactly `count` numbers in [lo, hi].
        bool Param(std::string_view value, std::size_t count, double lo, double hi, PreviewParam& out)
        {
            const auto colon = value.find(':');
            if (colon == std::string_view::npos || !IsName(value.substr(0, colon), 64)) return false;
            out.name = std::string(value.substr(0, colon));
            std::string_view rest = value.substr(colon + 1);
            for (std::size_t i = 0; i < count; ++i)
            {
                const auto comma = rest.find(',');
                const bool last = i + 1 == count;
                if (last != (comma == std::string_view::npos)) return false;
                if (!Number(last ? rest : rest.substr(0, comma), out.value[i]) || out.value[i] < lo || out.value[i] > hi) return false;
                if (!last) rest = rest.substr(comma + 1);
            }
            return true;
        }
    } // namespace

    std::string PreviewRequest::LookKey() const
    {
        std::string key = model + "|" + skin;
        char buffer[64];
        for (const auto& v : vectors)
        {
            std::snprintf(buffer, sizeof buffer, "|v:%.4f,%.4f,%.4f,%.4f", v.value[0], v.value[1], v.value[2], v.value[3]);
            key += "|" + v.name + buffer;
        }
        for (const auto& s : scalars)
        {
            std::snprintf(buffer, sizeof buffer, "|s:%.4f", s.value[0]);
            key += "|" + s.name + buffer;
        }
        return key;
    }

    std::optional<PreviewRequest> ParsePreviewRequest(std::string_view text, std::int64_t now, std::string* error)
    {
        auto bad = [error](const char* why) -> std::optional<PreviewRequest> {
            if (error) *error = why;
            return std::nullopt;
        };
        if (text.empty() || text.size() > MaxPreviewRequestBytes) return bad("size");
        PreviewRequest r;
        bool version = false, expires = false, seq = false, model = false, skin = false, yaw = false;
        while (!text.empty())
        {
            auto nl = text.find('\n');
            std::string_view line = text.substr(0, nl);
            text = nl == std::string_view::npos ? std::string_view{} : text.substr(nl + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            const auto eq = line.find('=');
            if (eq == std::string_view::npos) return bad("line");
            const std::string_view key = line.substr(0, eq), value = line.substr(eq + 1);
            for (char c : value)
                if (static_cast<unsigned char>(c) < 0x20) return bad("control");
            auto once = [&](bool& seen) {
                if (seen) return false;
                seen = true;
                return true;
            };
            if (key == "v")
            {
                if (!once(version) || value != "1") return bad("version");
            }
            else if (key == "expires")
            {
                if (!once(expires) || !Integer(value, r.expires)) return bad("expires");
            }
            else if (key == "seq")
            {
                std::int64_t n{};
                if (!once(seq) || !Integer(value, n) || n < 0) return bad("seq");
                r.seq = static_cast<std::uint64_t>(n);
            }
            else if (key == "model")
            {
                if (!once(model) || !IsName(value, 32)) return bad("model");
                r.model = std::string(value);
            }
            else if (key == "skin")
            {
                if (!once(skin) || (!value.empty() && !IsName(value, 32))) return bad("skin");
                r.skin = std::string(value);
            }
            else if (key == "yaw")
            {
                if (!once(yaw) || !Number(value, r.yaw) || r.yaw < -180.0 || r.yaw > 180.0) return bad("yaw");
            }
            else if (key == "vector")
            {
                PreviewParam p;
                if (r.vectors.size() >= MaxPreviewParams || !Param(value, 4, 0.0, 1.0, p)) return bad("vector");
                r.vectors.push_back(std::move(p));
            }
            else if (key == "scalar")
            {
                PreviewParam p;
                if (r.scalars.size() >= MaxPreviewParams || !Param(value, 1, -10.0, 10.0, p)) return bad("scalar");
                r.scalars.push_back(std::move(p));
            }
            // Unknown keys are ignored (newer services).
        }
        if (!version || !expires || !seq || !model) return bad("missing");
        if (r.expires <= now) return bad("expired");
        if (r.expires > now + MaxPreviewLifetime) return bad("too-far");
        return r;
    }

    PreviewDecision DecidePreview(const std::optional<PreviewRequest>& request, const PreviewGameState& s)
    {
        if (!request) return {false, "Cosmetics page not open"};
        if (s.inChallenge != false) return {false, "challenge or unknown challenge state"};
        if (s.benchmark != false) return {false, "benchmark or unknown benchmark state"};
        if (s.editor != false) return {false, "scenario editor or unknown editor state"};
        if (s.loading != false) return {false, "scenario loading"};
        return {true, "Cosmetics page open"};
    }

    std::string FormatPreviewFrame(std::uint64_t seq, std::string_view file, int width, int height)
    {
        return "v=1\nseq=" + std::to_string(seq) + "\nfile=" + std::string(file) + "\nwidth=" + std::to_string(width) + "\nheight=" + std::to_string(height) + "\n";
    }

    namespace
    {
        // sRGB <-> linear.
        struct Srgb
        {
            float toLinear[256];
            Srgb()
            {
                for (int i = 0; i < 256; ++i)
                {
                    const double c = i / 255.0;
                    toLinear[i] = static_cast<float>(c <= 0.04045 ? c / 12.92 : std::pow((c + 0.055) / 1.055, 2.4));
                }
            }
            static std::uint8_t Encode(double linear)
            {
                linear = std::clamp(linear, 0.0, 1.0);
                const double c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * std::pow(linear, 1.0 / 2.4) - 0.055;
                return static_cast<std::uint8_t>(std::lround(std::clamp(c, 0.0, 1.0) * 255.0));
            }
        };
        const Srgb& SrgbTable()
        {
            static const Srgb table;
            return table;
        }
        bool SameColour(const std::uint8_t* a, const std::uint8_t* b, int tolerance)
        {
            return std::abs(a[0] - b[0]) <= tolerance && std::abs(a[1] - b[1]) <= tolerance && std::abs(a[2] - b[2]) <= tolerance;
        }
    } // namespace

    double PreviewCameraDistance(double halfHeight, double halfWidth, double fovDegrees)
    {
        const double fov = std::clamp(fovDegrees, 10.0, 90.0) * 3.14159265358979 / 360.0;
        // Fit the height with a margin; the width counts as a radius so the
        // character never leaves the view while it turns.
        const double fit = std::max({halfHeight * 1.3, halfWidth * 1.45, 20.0});
        return fit / std::tan(fov) + halfWidth;
    }

    PreviewComposition ComposePreview(const PreviewPixels& color, const PreviewPixels& normals, int size)
    {
        PreviewComposition out;
        size = std::clamp(size, 16, 2048);
        out.image.width = out.image.height = size;
        out.image.rgba.assign(static_cast<std::size_t>(size) * size * 4, 255);
        const Srgb& srgb = SrgbTable();
        const bool inputs = color.Valid() && normals.Valid() && color.width == normals.width && color.height == normals.height;
        const int w = inputs ? color.width : 0, h = inputs ? color.height : 0;

        // Background normal: the value most corners agree on.
        std::vector<bool> mask;
        long long count = 0;
        out.left = w;
        out.top = h;
        out.right = out.bottom = -1;
        if (inputs)
        {
            const std::uint8_t* corners[4] = {&normals.rgba[0], &normals.rgba[(static_cast<std::size_t>(w) - 1) * 4],
                                              &normals.rgba[(static_cast<std::size_t>(h) - 1) * w * 4],
                                              &normals.rgba[((static_cast<std::size_t>(h) - 1) * w + w - 1) * 4]};
            const std::uint8_t* background = corners[0];
            int best = -1;
            for (const std::uint8_t* c : corners)
            {
                int votes = 0;
                for (const std::uint8_t* o : corners) votes += SameColour(c, o, 2);
                if (votes > best) best = votes, background = c;
            }
            mask.assign(static_cast<std::size_t>(w) * h, false);
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                {
                    const std::size_t i = static_cast<std::size_t>(y) * w + x;
                    if (SameColour(&normals.rgba[i * 4], background, 6)) continue;
                    mask[i] = true;
                    ++count;
                    out.left = std::min(out.left, x);
                    out.right = std::max(out.right, x);
                    out.top = std::min(out.top, y);
                    out.bottom = std::max(out.bottom, y);
                }
        }
        out.coverage = w && h ? static_cast<double>(count) / (static_cast<double>(w) * h) : 0;
        out.empty = count < std::max<long long>(16, static_cast<long long>(w) * h / 2000);

        // Levelling: the 97th percentile of the character's luminance goes to ~0.8.
        if (!out.empty)
        {
            std::vector<float> lum;
            lum.reserve(static_cast<std::size_t>(count));
            for (std::size_t i = 0; i < mask.size(); ++i)
                if (mask[i])
                {
                    const std::uint8_t* p = &color.rgba[i * 4];
                    lum.push_back(0.2126f * srgb.toLinear[p[0]] + 0.7152f * srgb.toLinear[p[1]] + 0.0722f * srgb.toLinear[p[2]]);
                }
            auto at = lum.begin() + static_cast<std::ptrdiff_t>(static_cast<double>(lum.size() - 1) * 0.97);
            std::nth_element(lum.begin(), at, lum.end());
            out.gain = *at > 1e-4f ? std::clamp(0.8 / *at, 0.7, 5.0) : 5.0;
        }

        // Framing on the silhouette: height-led so turning does not zoom.
        double cx = w / 2.0, cy = h / 2.0, span = std::max(w, h);
        if (!out.empty)
        {
            const double bw = out.right - out.left + 1.0, bh = out.bottom - out.top + 1.0;
            span = std::max({bh * 1.18, bw * 1.12, 16.0});
            cx = (out.left + out.right + 1) / 2.0;
            cy = (out.top + out.bottom + 1) / 2.0;
        }
        const double x0 = cx - span / 2, y0 = cy - span / 2, scale = span / size;
        // Floor shadow under the feet, in output coordinates.
        const double feetY = out.empty ? size * 0.9 : (out.bottom + 1 - y0) / scale, feetX = size / 2.0;
        const double shadowRx = size * 0.24, shadowRy = size * 0.035;

        constexpr int Taps = 3;
        for (int oy = 0; oy < size; ++oy)
            for (int ox = 0; ox < size; ++ox)
            {
                // Backdrop: a soft radial falloff from just above the centre, plus the shadow.
                const double dx = (ox + 0.5) / size - 0.5, dy = (oy + 0.5) / size - 0.42;
                const double t = std::clamp(std::sqrt(dx * dx + dy * dy) / 0.72, 0.0, 1.0);
                double back[3];
                for (int c = 0; c < 3; ++c)
                    back[c] = srgb.toLinear[PreviewBackdropCentre[c]] + (srgb.toLinear[PreviewBackdropEdge[c]] - srgb.toLinear[PreviewBackdropCentre[c]]) * t;
                const double sx = (ox + 0.5 - feetX) / shadowRx, sy = (oy + 0.5 - feetY) / shadowRy;
                const double shadow = out.empty ? 0 : std::clamp(1.0 - (sx * sx + sy * sy), 0.0, 1.0) * 0.45;
                for (double& b : back) b *= 1.0 - shadow;

                double sum[3] = {0, 0, 0};
                for (int ty = 0; ty < Taps; ++ty)
                    for (int tx = 0; tx < Taps; ++tx)
                    {
                        const int x = static_cast<int>(std::floor(x0 + (ox + (tx + 0.5) / Taps) * scale));
                        const int y = static_cast<int>(std::floor(y0 + (oy + (ty + 0.5) / Taps) * scale));
                        const std::size_t i = static_cast<std::size_t>(y) * w + x;
                        if (!out.empty && x >= 0 && y >= 0 && x < w && y < h && mask[i])
                        {
                            const std::uint8_t* p = &color.rgba[i * 4];
                            for (int c = 0; c < 3; ++c) sum[c] += std::min(1.0, srgb.toLinear[p[c]] * out.gain);
                        }
                        else
                            for (int c = 0; c < 3; ++c) sum[c] += back[c];
                    }
                std::uint8_t* o = &out.image.rgba[(static_cast<std::size_t>(oy) * size + ox) * 4];
                for (int c = 0; c < 3; ++c) o[c] = Srgb::Encode(sum[c] / (Taps * Taps));
                o[3] = 255;
            }
        return out;
    }
} // namespace aimmod
