#pragma once
// AimMod runtime meshes (in-game/docs/cosmetics.md, "Runtime meshes"): rigid
// accessory shapes AimModCore builds at runtime as a ProceduralMeshComponent,
// so no UE editor or pak is needed. Shipped as .amsh files next to the
// catalog, pinned by size and SHA-256 in catalog-manifest.json.
//
// .amsh, little-endian:
//   "AMSH"  u32 version (1)  u32 vertices  u32 indices
//   f32 position[3] x vertices      cm; X forward, Y right, Z up (UE)
//   f32 normal[3]   x vertices      unit length
//   f32 uv[2]       x vertices
//   u8  colour[4]   x vertices      RGBA (sRGB)
//   u32 index       x indices       triangles, counter-clockwise seen from outside
// Validated whole (sizes, counts, finite values, indices in range); nothing
// partial is ever used.
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace aimmod::mesh
{
    struct Vec3
    {
        float x{}, y{}, z{};
    };
    struct Mesh
    {
        std::vector<Vec3> positions, normals;
        std::vector<float> uvs;              // 2 per vertex
        std::vector<std::uint8_t> colours;   // 4 per vertex
        std::vector<std::uint32_t> indices;  // 3 per triangle
        bool Valid() const;
        void Bounds(double min[3], double max[3]) const;
    };

    inline constexpr std::uint32_t MaxVertices = 65536, MaxIndices = 3 * 65536;
    inline constexpr std::size_t MaxFileBytes = 8u << 20;

    std::optional<Mesh> Parse(std::string_view bytes, std::string* error = nullptr);
    std::string Serialize(const Mesh& mesh);
    // A mesh file name the catalog may name: "<lowercase id>.amsh".
    bool IsMeshName(std::string_view name);

    // Generators for the first AimMod shapes. All centred on the origin, Z up.
    // A ring around Z: `radius` to the tube centre, an elliptical tube profile
    // (half-width `tubeR` across the ring, half-height `tubeZ`).
    Mesh Ring(float radius, float tubeR, float tubeZ, int around, int profile, std::uint8_t r, std::uint8_t g, std::uint8_t b);
    // A visor: a curved band in front (+X) of a head of `radius`, spanning
    // `arcDegrees`, `height` tall and `thickness` thick, with rounded ends.
    Mesh Visor(float radius, float height, float thickness, float arcDegrees, int segments, std::uint8_t r, std::uint8_t g, std::uint8_t b);

    // The meshes AimMod ships (in-game/cosmetics/meshes/<name>), generated
    // here so the files are reproducible: aimmod_core_tests --write-meshes <dir>.
    std::vector<std::pair<std::string, Mesh>> Shipped();
} // namespace aimmod::mesh
