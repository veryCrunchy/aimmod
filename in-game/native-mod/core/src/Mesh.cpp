#include <aimmod/Mesh.hpp>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace aimmod::mesh
{
    namespace
    {
        constexpr float Pi = 3.14159265358979f;
        template <class T>
        bool Read(std::string_view& in, T& out)
        {
            if (in.size() < sizeof(T)) return false;
            std::memcpy(&out, in.data(), sizeof(T));
            in.remove_prefix(sizeof(T));
            return true;
        }
        template <class T>
        void Write(std::string& out, const T& v)
        {
            out.append(reinterpret_cast<const char*>(&v), sizeof(T));
        }
        Vec3 Normal(float x, float y, float z)
        {
            const float l = std::sqrt(x * x + y * y + z * z);
            return l > 1e-12f ? Vec3{x / l, y / l, z / l} : Vec3{0, 0, 1};
        }
        void Colour(Mesh& m, std::uint8_t r, std::uint8_t g, std::uint8_t b)
        {
            for (std::size_t i = 0; i < m.positions.size(); ++i) m.colours.insert(m.colours.end(), {r, g, b, 255});
        }
        // A closed loop of `profile` points around a tube centre, swept along `around`.
        void Grid(Mesh& m, int around, int profile, bool closedAround)
        {
            const int columns = closedAround ? around : around - 1;
            for (int i = 0; i < columns; ++i)
                for (int j = 0; j < profile; ++j)
                {
                    const auto a = static_cast<std::uint32_t>(i * profile + j), b = static_cast<std::uint32_t>(((i + 1) % around) * profile + j),
                               c = static_cast<std::uint32_t>(((i + 1) % around) * profile + (j + 1) % profile), d = static_cast<std::uint32_t>(i * profile + (j + 1) % profile);
                    m.indices.insert(m.indices.end(), {a, b, c, a, c, d});
                }
        }
    } // namespace

    bool Mesh::Valid() const
    {
        const std::size_t n = positions.size();
        if (n == 0 || n > MaxVertices || normals.size() != n || uvs.size() != 2 * n || colours.size() != 4 * n) return false;
        if (indices.empty() || indices.size() > MaxIndices || indices.size() % 3) return false;
        for (std::uint32_t i : indices)
            if (i >= n) return false;
        for (const Vec3& p : positions)
            if (!std::isfinite(p.x) || !std::isfinite(p.y) || !std::isfinite(p.z) || std::abs(p.x) > 500 || std::abs(p.y) > 500 || std::abs(p.z) > 500) return false;
        for (const Vec3& v : normals)
            if (!std::isfinite(v.x) || !std::isfinite(v.y) || !std::isfinite(v.z)) return false;
        for (float u : uvs)
            if (!std::isfinite(u)) return false;
        return true;
    }

    void Mesh::Bounds(double min[3], double max[3]) const
    {
        for (int k = 0; k < 3; ++k) min[k] = 1e300, max[k] = -1e300;
        for (const Vec3& p : positions)
        {
            const float v[3] = {p.x, p.y, p.z};
            for (int k = 0; k < 3; ++k) min[k] = std::min<double>(min[k], v[k]), max[k] = std::max<double>(max[k], v[k]);
        }
    }

    std::optional<Mesh> Parse(std::string_view in, std::string* error)
    {
        auto fail = [&](const char* why) -> std::optional<Mesh> {
            if (error) *error = why;
            return std::nullopt;
        };
        if (in.size() > MaxFileBytes) return fail("too large");
        if (in.size() < 16 || in.substr(0, 4) != "AMSH") return fail("not an AimMod mesh");
        in.remove_prefix(4);
        std::uint32_t version = 0, vertices = 0, count = 0;
        Read(in, version), Read(in, vertices), Read(in, count);
        if (version != 1) return fail("unknown version");
        if (vertices == 0 || vertices > MaxVertices || count == 0 || count > MaxIndices || count % 3) return fail("bad counts");
        const std::size_t need = static_cast<std::size_t>(vertices) * (12 + 12 + 8 + 4) + static_cast<std::size_t>(count) * 4;
        if (in.size() != need) return fail("wrong size");
        Mesh m;
        m.positions.resize(vertices), m.normals.resize(vertices), m.uvs.resize(2u * vertices), m.colours.resize(4u * vertices), m.indices.resize(count);
        std::memcpy(m.positions.data(), in.data(), vertices * 12u), in.remove_prefix(vertices * 12u);
        std::memcpy(m.normals.data(), in.data(), vertices * 12u), in.remove_prefix(vertices * 12u);
        std::memcpy(m.uvs.data(), in.data(), vertices * 8u), in.remove_prefix(vertices * 8u);
        std::memcpy(m.colours.data(), in.data(), vertices * 4u), in.remove_prefix(vertices * 4u);
        std::memcpy(m.indices.data(), in.data(), count * 4u);
        if (!m.Valid()) return fail("bad values");
        return m;
    }

    std::string Serialize(const Mesh& m)
    {
        std::string out = "AMSH";
        Write(out, std::uint32_t{1});
        Write(out, static_cast<std::uint32_t>(m.positions.size()));
        Write(out, static_cast<std::uint32_t>(m.indices.size()));
        out.append(reinterpret_cast<const char*>(m.positions.data()), m.positions.size() * 12);
        out.append(reinterpret_cast<const char*>(m.normals.data()), m.normals.size() * 12);
        out.append(reinterpret_cast<const char*>(m.uvs.data()), m.uvs.size() * 4);
        out.append(reinterpret_cast<const char*>(m.colours.data()), m.colours.size());
        out.append(reinterpret_cast<const char*>(m.indices.data()), m.indices.size() * 4);
        return out;
    }

    bool IsMeshName(std::string_view name)
    {
        constexpr std::string_view ext = ".amsh";
        if (name.size() <= ext.size() || name.size() > 48 || name.substr(name.size() - ext.size()) != ext) return false;
        const std::string_view stem = name.substr(0, name.size() - ext.size());
        if (stem.front() == '-') return false;
        return std::all_of(stem.begin(), stem.end(), [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'; });
    }

    Mesh Ring(float radius, float tubeR, float tubeZ, int around, int profile, std::uint8_t r, std::uint8_t g, std::uint8_t b)
    {
        Mesh m;
        around = std::clamp(around, 8, 256), profile = std::clamp(profile, 4, 64);
        for (int i = 0; i < around; ++i)
        {
            const float u = 2 * Pi * i / around, cu = std::cos(u), su = std::sin(u);
            for (int j = 0; j < profile; ++j)
            {
                const float v = 2 * Pi * j / profile, cv = std::cos(v), sv = std::sin(v);
                // Elliptical profile: across the ring by tubeR, up by tubeZ.
                const float d = radius + tubeR * cv;
                m.positions.push_back({d * cu, d * su, tubeZ * sv});
                // Normal of the ellipse (scaled by the other radius), swept around.
                m.normals.push_back(Normal(cv * tubeZ * cu, cv * tubeZ * su, sv * tubeR));
                m.uvs.insert(m.uvs.end(), {static_cast<float>(i) / around, static_cast<float>(j) / profile});
            }
        }
        Grid(m, around, profile, true);
        Colour(m, r, g, b);
        return m;
    }

    Mesh Visor(float radius, float height, float thickness, float arcDegrees, int segments, std::uint8_t r, std::uint8_t g, std::uint8_t b)
    {
        Mesh m;
        segments = std::clamp(segments, 4, 256);
        const float arc = std::clamp(arcDegrees, 10.0f, 300.0f) * Pi / 180;
        // Cross-section: a rounded rectangle (stadium), `thickness` wide, `height` tall.
        constexpr int Profile = 16;
        const float rr = thickness / 2, half = std::max(0.0f, height / 2 - rr);
        auto section = [&](int j, float& radial, float& up, float& nr, float& nz) {
            // Upper cap for j < Profile/2, lower cap after.
            const int k = j % (Profile / 2);
            const float t = Pi * k / (Profile / 2 - 1);
            const bool top = j < Profile / 2;
            const float a = top ? t : Pi + t;
            nr = std::cos(a), nz = std::sin(a);
            radial = rr * nr;
            up = rr * nz + (top ? half : -half);
        };
        for (int i = 0; i <= segments; ++i)
        {
            const float u = -arc / 2 + arc * i / segments, cu = std::cos(u), su = std::sin(u);
            for (int j = 0; j < Profile; ++j)
            {
                float radial, up, nr, nz;
                section(j, radial, up, nr, nz);
                const float d = radius + radial;
                m.positions.push_back({d * cu, d * su, up});
                m.normals.push_back(Normal(nr * cu, nr * su, nz));
                m.uvs.insert(m.uvs.end(), {static_cast<float>(i) / segments, static_cast<float>(j) / Profile});
            }
        }
        Grid(m, segments + 1, Profile, false);
        // Caps at both ends: a fan around each end section's centre.
        for (int end = 0; end < 2; ++end)
        {
            const float u = end == 0 ? -arc / 2 : arc / 2;
            const Vec3 n = end == 0 ? Normal(std::sin(u), -std::cos(u), 0) : Normal(-std::sin(u), std::cos(u), 0);
            const auto centre = static_cast<std::uint32_t>(m.positions.size());
            m.positions.push_back({radius * std::cos(u), radius * std::sin(u), 0});
            m.normals.push_back(n);
            m.uvs.insert(m.uvs.end(), {0.5f, 0.5f});
            const auto first = static_cast<std::uint32_t>(m.positions.size());
            const int column = end == 0 ? 0 : segments;
            for (int j = 0; j < Profile; ++j)
            {
                m.positions.push_back(m.positions[static_cast<std::size_t>(column * Profile + j)]);
                m.normals.push_back(n);
                m.uvs.insert(m.uvs.end(), {0.5f, 0.5f});
            }
            for (int j = 0; j < Profile; ++j)
            {
                const auto a = first + static_cast<std::uint32_t>(j), b2 = first + static_cast<std::uint32_t>((j + 1) % Profile);
                if (end == 0) m.indices.insert(m.indices.end(), {centre, b2, a});
                else m.indices.insert(m.indices.end(), {centre, a, b2});
            }
        }
        Colour(m, r, g, b);
        return m;
    }
    std::vector<std::pair<std::string, Mesh>> Shipped()
    {
        // Sizes in cm for a 22 cm head (AimModCore scales them to each model's head).
        return {
            {"halo.amsh", Ring(11.0f, 0.75f, 0.5f, 96, 16, 255, 255, 255)},     // a thin, flat ring
            {"collar.amsh", Ring(10.5f, 1.3f, 2.2f, 96, 20, 255, 255, 255)},    // a tall, rounded neck ring
            {"visor.amsh", Visor(12.5f, 4.5f, 0.9f, 150.0f, 64, 255, 255, 255)}, // a curved band across the eyes
        };
    }
} // namespace aimmod::mesh
