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
        // Everything but the rotation: a change re-applies the look.
        std::string LookKey() const;
    };

    inline constexpr std::size_t MaxPreviewRequestBytes = 2048;
    inline constexpr std::int64_t MaxPreviewLifetime = 15;
    inline constexpr std::size_t MaxPreviewParams = 8;

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
} // namespace aimmod
