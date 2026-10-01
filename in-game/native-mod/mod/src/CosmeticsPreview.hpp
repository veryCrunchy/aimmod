#pragma once
// Live character preview for the AimMod Cosmetics page (in-game/docs/cosmetics.md,
// "Character preview"). Game thread only.
//
// While the service's cosmetics-preview.txt request is fresh and the game is
// not in a challenge, benchmark, the scenario editor or loading, this spawns
// the game's own character preview stage (CharacterSkinPreviewActorUserInterfaceBP_C)
// far above the map, points its scene capture at an AimMod render target,
// applies the requested Default-pack model and skin and the resolved catalog
// parameters, and exports a PNG on every change. The service serves the PNG
// to the page. Nothing else in the world is touched; the stage is destroyed
// as soon as the request goes stale or the gate closes.
#include "GameBindings.hpp"

#include <aimmod/CosmeticsPreview.hpp>

#include <Unreal/FWeakObjectPtr.hpp>

#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace aimmod
{
    namespace game
    {
        struct Scene;
    }

    using game::UClass;
    using game::UObject;

    class CosmeticsPreview
    {
    public:
        CosmeticsPreview(game::Scene& scene, std::filesystem::path root);
        void Bind();
        // Every engine frame; inChallenge / loading as the observer knows them.
        void Tick(double now, bool inChallenge, bool loading);
        void Shutdown();

    private:
        std::optional<PreviewRequest> ReadRequest();
        PreviewGameState GameState(bool inChallenge, bool loading) const;
        bool EnsureStage(UObject* world);
        void Teardown(const char* why);
        void ApplyLook(const PreviewRequest& request);
        void ApplyRotation(double yaw);
        bool Capture();
        bool FreeLook(const std::string& model, const std::string& skin) const;

        game::Scene& m_scene;
        std::filesystem::path m_root, m_requestPath, m_framePath, m_frames;
        bool m_bound{}, m_available{};
        std::string m_unavailable;
        game::Getter m_isBenchmark, m_isEditor;
        UClass* m_stageClass{};

        RC::Unreal::FWeakObjectPtr m_stage, m_target, m_capture, m_meshes, m_mesh;
        UObject* m_world{};
        double m_baseYaw{};
        std::vector<UObject*> m_originals; // per material slot of the stage mesh
        std::string m_lookKey;
        double m_yaw{1e9};
        std::uint64_t m_seq{};
        bool m_dirty{};
        double m_nextRead{}, m_nextCapture{};
        std::vector<double> m_followUps; // re-captures after a look change (streaming, fade-in)
        int m_frameIndex{};
        std::uint64_t m_frameSeq{};
        std::string m_lastReason;
    };
} // namespace aimmod
