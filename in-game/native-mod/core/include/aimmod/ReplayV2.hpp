#pragma once
// Compact replay format (format 2). Source of truth is the input stream the
// game consumed (mouse axis values per engine frame, button edges) plus the
// constants that turn it into view rotation; exact rotation is stored only as
// sparse keyframes that correct drift. Everything else (camera location, FOV,
// targets, appearance) is stored as piecewise-linear keyframes within a
// tolerance, and stats as change records. See DESIGN.md "Replay format 2".
#include <aimmod/Formats.hpp>

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace aimmod::replay2
{
    inline constexpr char Magic[8] = {'A', 'M', 'R', 'P', 'L', 'A', 'Y', '2'};

    // Index into ReplayCatalog's action list (IsReplayAction order).
    int ActionIndex(std::string_view action);
    const char* ActionName(int index);
    inline bool IsAxis(int index) { return index >= 0 && index < 4; }
    inline constexpr int AxisTurn = 0, AxisLookUp = 1;

    struct InputEvent
    {
        std::uint32_t frame{};
        std::uint8_t action{};
        float value{};
    };
    struct CameraSample
    {
        std::uint32_t frame{};
        double x{}, y{}, z{}, pitch{}, yaw{}, roll{}, fov{};
    };
    struct ActorSample
    {
        std::uint32_t frame{};
        std::uint32_t id{};
        double x{}, y{}, z{}, radius{}, halfHeight{};
        std::optional<double> health;
        bool hasRotation{};
        double pitch{}, yaw{}, roll{};
    };
    struct StatsSample
    {
        std::uint32_t frame{};
        std::optional<double> score, shots, hits, kills, damage, seconds;
    };
    struct HitEvent
    {
        std::uint32_t frame{};
        std::uint32_t target{};
    };

    // Everything captured for one attempt. Frame numbers index frameTimes
    // (seconds since the attempt started, strictly increasing): the engine
    // frames observed while recording. Inputs belong to the frame in which the
    // game consumed them; samples are the state after that frame.
    struct Capture
    {
        ReplayHeader header;
        std::vector<double> frameTimes;
        std::vector<InputEvent> inputs;
        std::vector<CameraSample> camera;
        std::vector<ActorSample> actors;
        std::unordered_map<std::uint32_t, std::string> profiles;
        std::vector<StatsSample> stats;
        std::vector<HitEvent> hits;
        std::string reason;
        std::optional<double> score;
    };

    struct EncodeOptions
    {
        double rotationTolerance = 0.02;   // degrees; drift above this adds a keyframe
        double maxKeyframeInterval = 0.5;  // seconds
        double positionTolerance = 0.5;    // centimetres (targets)
        double cameraTolerance = 0.05;     // centimetres (camera location)
        double angleTolerance = 0.5;       // degrees (target appearance rotation)
        double maxSegmentSeconds = 2.0;    // longest interpolated span
        std::uint32_t compression = 5;     // Windows Compression API algorithm (5 LZMS, 4 XPRESS_HUFF, 0 none)
    };

    struct EncodeReport
    {
        std::size_t bodyBytes{};  // before compression
        std::size_t fileBytes{};
        std::uint32_t engineFrames{}, inputEvents{}, cameraSamples{}, keyframes{};
        std::uint32_t actorSamples{}, actorPoints{};
        double quantum{};
        double yawPerUnit{}, pitchPerUnit{};
        double keyframeErrorMax{}, keyframeErrorRms{}; // reconstruction drift at keyframes, before snapping
        double sampleErrorMax{};                        // worst error at any camera sample after encoding
        double duration{};
    };

    // Returns an empty vector if the capture is unusable.
    std::vector<std::uint8_t> Encode(const Capture& capture, const EncodeOptions& options, EncodeReport* report = nullptr);

    // Decoded representation (engine independent; mirrors the C# reader).
    struct Keyframe
    {
        std::uint32_t frame{};
        double pitch{}, yaw{}, roll{};
    };
    struct Point
    {
        std::uint32_t frame{};
        bool segmentStart{};
        double v[5]{}; // camera: x y z fov -; actor: x y z radius halfHeight; rotation: pitch yaw roll
    };
    struct Track
    {
        std::uint32_t id{};
        std::string profile;
        std::vector<Point> points;
        std::vector<std::pair<std::uint32_t, double>> health;
        std::vector<Point> rotation;
    };
    struct StatsRecord
    {
        std::uint32_t frame{};
        std::uint8_t mask{};
        double values[6]{};
    };
    struct Decoded
    {
        std::string headerJson;
        std::vector<double> frameTimes;
        double quantum{};
        std::vector<InputEvent> inputs;
        double yawPerUnit{}, pitchPerUnit{};
        std::vector<Keyframe> keyframes;
        std::vector<Point> camera; // x y z fov
        std::vector<Track> actors;
        std::vector<StatsRecord> stats;
        std::vector<HitEvent> hits;

        // Reconstructed view rotation after engine frame `frame`.
        void Rotation(std::uint32_t frame, double out[3]) const;
    };
    std::optional<Decoded> Decode(const std::uint8_t* data, std::size_t size);

    // Windows Compression API helpers (algorithm 0 = copy).
    bool Compress(std::uint32_t algorithm, const std::vector<std::uint8_t>& in, std::vector<std::uint8_t>& out);
    bool Decompress(std::uint32_t algorithm, const std::uint8_t* in, std::size_t size, std::vector<std::uint8_t>& out);
} // namespace aimmod::replay2
