#pragma once
// Remote-player transform math, shared by the mod and its tests. Inputs are
// only the remote player's received samples and the render time; nothing
// local (camera, capsule, crouch) can reach a remote transform.

#include "Codec.hpp"

#include <algorithm>
#include <cmath>
#include <vector>

namespace bridge::ghost
{
    constexpr double InterpolationDelay = 0.1; // seconds behind the newest sample
    constexpr double MaxExtrapolation = 0.1;
    constexpr float DefaultHalfHeight = 88.f;  // UE default capsule, used when a sender doesn't report one
    constexpr float DefaultRadius = 34.f;

    struct TimedPose
    {
        double time; // local receive time (seconds)
        Pose pose;
    };

    struct RemoteTransform
    {
        double x = 0, y = 0, z = 0; // remote actor location (capsule centre), Z included
        double yaw = 0, pitch = 0;
        double vx = 0, vy = 0, vz = 0;
        bool crouch = false;        // the remote player's crouch flag
        double halfHeight = DefaultHalfHeight; // the remote player's capsule half-height
    };

    inline double WrapAngle(double a)
    {
        a = std::fmod(a + 180.0, 360.0);
        if (a < 0) a += 360.0;
        return a - 180.0;
    }
    inline double LerpAngle(double a, double b, double t) { return a + WrapAngle(b - a) * t; }

    inline RemoteTransform FromPose(const Pose& p)
    {
        RemoteTransform r;
        r.x = p.x, r.y = p.y, r.z = p.z;
        r.yaw = p.yaw, r.pitch = p.pitch;
        r.vx = p.vx, r.vy = p.vy, r.vz = p.vz;
        r.crouch = (p.flags & PoseFlagCrouch) != 0;
        r.halfHeight = (p.flags & PoseFlagHalfHeight) && p.halfHeight > 5 && p.halfHeight < 1000 ? p.halfHeight : DefaultHalfHeight;
        return r;
    }

    // Interpolates `delay` seconds behind the newest sample; extrapolates from
    // velocity for at most MaxExtrapolation when samples run out.
    inline RemoteTransform Sample(const std::vector<TimedPose>& samples, double now, double delay = InterpolationDelay)
    {
        if (samples.empty()) return {};
        const double t = now - delay;
        if (t <= samples.front().time) return FromPose(samples.front().pose);
        if (t >= samples.back().time)
        {
            RemoteTransform r = FromPose(samples.back().pose);
            const double dt = std::min(t - samples.back().time, MaxExtrapolation);
            r.x += r.vx * dt;
            r.y += r.vy * dt;
            r.z += r.vz * dt;
            return r;
        }
        std::size_t i = 1;
        while (i < samples.size() && samples[i].time < t) ++i;
        const RemoteTransform a = FromPose(samples[i - 1].pose), b = FromPose(samples[i].pose);
        const double span = samples[i].time - samples[i - 1].time;
        const double k = span > 0 ? (t - samples[i - 1].time) / span : 1;
        RemoteTransform r = b; // discrete state (crouch, height) from the newer sample
        r.x = a.x + (b.x - a.x) * k;
        r.y = a.y + (b.y - a.y) * k;
        r.z = a.z + (b.z - a.z) * k;
        r.yaw = LerpAngle(a.yaw, b.yaw, k);
        r.pitch = a.pitch + (b.pitch - a.pitch) * k;
        r.vx = a.vx + (b.vx - a.vx) * k;
        r.vy = a.vy + (b.vy - a.vy) * k;
        r.vz = a.vz + (b.vz - a.vz) * k;
        r.halfHeight = a.halfHeight + (b.halfHeight - a.halfHeight) * k;
        return r;
    }

    // Engine basic-shape fallback layout (shapes are 100 units). Everything
    // comes from the remote transform.
    struct ShapePart
    {
        double x, y, z;    // world location
        double sx, sy, sz; // scale
    };
    struct ShapeLayout
    {
        ShapePart body, head, visor;
    };
    inline ShapeLayout Layout(const RemoteTransform& r, double radius = DefaultRadius)
    {
        const double h = r.halfHeight; // already the crouched height when the sender crouches
        const double d = radius * 2 / 100.0;
        const double rad = r.yaw * 3.14159265358979 / 180.0;
        const double headZ = r.z + h * 0.85;
        ShapeLayout l;
        l.body = {r.x, r.y, r.z - h * 0.1, d, d, h * 1.6 / 100.0};
        l.head = {r.x, r.y, headZ, d * 0.9, d * 0.9, d * 0.9};
        l.visor = {r.x + std::cos(rad) * radius, r.y + std::sin(rad) * radius, headZ, d * 0.35, d * 0.7, d * 0.2};
        return l;
    }
} // namespace bridge::ghost
