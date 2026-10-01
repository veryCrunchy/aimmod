#pragma once
// Live character preview for the AimMod Cosmetics page (in-game/docs/cosmetics.md,
// "Character preview"). Game thread only.
//
// While the service's cosmetics-preview.txt request is fresh and the game is
// not in a challenge, benchmark, the scenario editor or loading, this spawns
// the game's own character preview stage (CharacterSkinPreviewActorUserInterfaceBP_C)
// far above the map, points its scene capture at an AimMod render target,
// dresses its skeletal mesh with the requested Default-pack model and skin
// (loaded from the game's Default packs) and the resolved catalog
// parameters, lights it with its own short-range rig at a fixed exposure,
// frames the camera on the character and, on every change, captures colour
// and normals and composes the PNG the service serves (ComposePreview).
// Nothing else in the world is touched; the stage is destroyed as soon as the
// request goes stale or the gate closes.
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
        void Frame();
        bool Capture();
        bool CaptureTo(std::uint8_t source, const std::wstring& file);

        // The requested look, from the game's free Default packs only.
        struct Look
        {
            UObject* mesh{};
            UClass* anim{};
            std::vector<UObject*> materials;
        };
        std::optional<Look> FreeLook(const std::string& model, const std::string& skin);

        game::Scene& m_scene;
        std::filesystem::path m_root, m_requestPath, m_framePath, m_frames;
        bool m_bound{}, m_available{};
        std::string m_unavailable;
        game::Getter m_isBenchmark, m_isEditor, m_isInChallenge;
        UClass* m_stageClass{};

        RC::Unreal::FWeakObjectPtr m_stage, m_target, m_capture, m_meshes, m_mesh;
        std::vector<RC::Unreal::FWeakObjectPtr> m_lights; // key, fill, rim
        UObject* m_world{};
        double m_baseYaw{};
        float m_cameraHome[3]{}; // the stage camera's own position: its front view
        bool m_logPacks{true}, m_loggedEmpty{};
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
