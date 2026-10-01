#pragma once
// Cosmetics page preview (in-game/docs/cosmetics.md, "Character preview").
//
// The service writes cosmetics-preview.txt while the Cosmetics page is open
// and visible, and refreshes it every second:
//   v=1
//   expires=<unix seconds>           now < expires <= now + 15
//   seq=<n>                          bumps on every change
//   model=<Default-pack model>       e.g. Meso, Endo
//   skin=<Default-pack skin>         optional
//   yaw=<degrees>                    -180..180, the drag rotation
//   vector=<Param>:r,g,b,a           up to 8, each 0..1 (resolved catalog items)
//   scalar=<Param>:value             up to 8, -10..10
//   accessory=<catalog item id>      up to 3, worn accessories (resolved by AimModCore)
// AimModCore renders the preview only while a fresh request exists and the
// game is not in a challenge, benchmark, the scenario editor or loading, and
// answers with cosmetics-preview-frame.txt naming the newest PNG.
// Engine-independent; game thread callers only.
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace aimmod
{
    struct PreviewParam
    {
        std::string name;
        double value[4]{}; // vector: r,g,b,a; scalar: value[0]
    };

    struct PreviewRequest
    {
        std::int64_t expires{};
        std::uint64_t seq{};
        std::string model, skin;
        double yaw{};
        std::vector<PreviewParam> vectors, scalars;
        std::vector<std::string> accessories; // catalog item ids
        // Everything but the rotation: a change re-applies the look.
        std::string LookKey() const;
    };

    inline constexpr std::size_t MaxPreviewRequestBytes = 2048;
    inline constexpr std::int64_t MaxPreviewLifetime = 15;
    inline constexpr std::size_t MaxPreviewParams = 8, MaxPreviewAccessories = 3;

    // Whole-file validation; nullopt (with `error`) if anything is off.
    std::optional<PreviewRequest> ParsePreviewRequest(std::string_view text, std::int64_t now, std::string* error = nullptr);

    // Game state the preview may run in. Unknown (nullopt) counts as "no".
    struct PreviewGameState
    {
        std::optional<bool> inChallenge, benchmark, editor, loading;
    };
    struct PreviewDecision
    {
        bool run{};
        const char* reason{};
    };
    PreviewDecision DecidePreview(const std::optional<PreviewRequest>& request, const PreviewGameState& state);

    // cosmetics-preview-frame.txt: "v=1\nseq=<n>\nfile=<name>\nwidth=<w>\nheight=<h>\n".
    std::string FormatPreviewFrame(std::uint64_t seq, std::string_view file, int width, int height);

    // ------------------------------------------------------- frame composition
    //
    // AimModCore captures the stage at PreviewSupersample x the output size:
    // once as final colour and once for the mask, a scene-colour capture whose
    // alpha is inverse opacity (or, if that alpha is unusable, world normals,
    // whose empty background is one constant value). Nothing but the stage is
    // rendered. ComposePreview turns the two captures into the frame the page
    // shows:
    //   - the character is cut out with that mask (no dependence on how dark
    //     the map's sky or fog is) and placed on a studio backdrop in the
    //     page's colours, with a soft floor shadow;
    //   - its brightness is evened out mildly (gain 0.75 to 2), so a dark
    //     material stays dark; the light rig does the real work;
    //   - it is framed on its own silhouette: centred, with a margin, scaled
    //     on its height so turning it does not zoom;
    //   - the result is box-filtered down to `size` (anti-aliased edges).
    struct PreviewPixels
    {
        int width{}, height{};
        std::vector<std::uint8_t> rgba; // 8-bit sRGB, row-major, 4 bytes per pixel
        bool Valid() const { return width > 0 && height > 0 && rgba.size() == static_cast<std::size_t>(width) * height * 4; }
    };
    struct PreviewComposition
    {
        PreviewPixels image;
        bool empty{true};       // no character pixels: backdrop only
        double gain{1};         // brightness levelling applied
        double coverage{};      // share of capture pixels that are character
        int left{}, top{}, right{}, bottom{}; // character bounds in the capture
    };
    inline constexpr int PreviewSize = 384, PreviewSupersample = 2;
    inline constexpr std::uint8_t PreviewBackdropCentre[3] = {0x20, 0x2d, 0x28}, PreviewBackdropEdge[3] = {0x12, 0x1a, 0x17};
    // Which capture marks the character: a scene-colour capture's alpha
    // (inverse opacity: low on the character, high where nothing rendered),
    // or a capture whose empty background is one constant colour (normals).
    enum class PreviewMask
    {
        InverseAlpha,
        Background,
    };
    PreviewComposition ComposePreview(const PreviewPixels& color, const PreviewPixels& mask, PreviewMask kind, int size);

    // Camera distance (cm) that fits a character of `halfHeight` and
    // `halfWidth` into a square view with `fovDegrees`, with room to turn.
    double PreviewCameraDistance(double halfHeight, double halfWidth, double fovDegrees);
} // namespace aimmod
