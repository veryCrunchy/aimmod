"""Reference Source (Quake-style) movement simulation used to sanity-check movement presets.

KovaaK's "Quake/Source movement" fields mirror these terms: ScaledGroundAcceleration = accelerate,
ContinuousGroundFriction = friction, StopSpeed/StopSpeedThreshold = stopspeed,
ScaledAirAcceleration = airaccelerate, MaxAirSpeed = air wish-speed cap. KovaaK's exact code is not
public, so this models Source itself; tests use it to confirm a preset jumps, lands and strafes like CS.
"""
from __future__ import annotations

import math
from typing import Tuple

from .scenario import Movement

TICK = 1.0 / 64.0


def friction(vx: float, vy: float, mv: Movement, dt: float) -> Tuple[float, float]:
    speed = math.hypot(vx, vy)
    if speed < 0.1:
        return 0.0, 0.0
    control = max(speed, mv.stop_speed)
    new = max(0.0, speed - control * mv.friction * dt)
    return vx * new / speed, vy * new / speed


def accelerate(vx, vy, wx, wy, wishspeed, accel, dt, cap=None):
    """Source Accelerate / AirAccelerate: only the component along wishdir is topped up."""
    lim = wishspeed if cap is None else min(wishspeed, cap)
    cur = vx * wx + vy * wy
    add = lim - cur
    if add <= 0:
        return vx, vy
    step = min(accel * wishspeed * dt, add)
    return vx + step * wx, vy + step * wy


def run_up(mv: Movement, seconds: float = 2.0) -> float:
    vx = vy = 0.0
    for _ in range(int(seconds / TICK)):
        vx, vy = friction(vx, vy, mv, TICK)
        vx, vy = accelerate(vx, vy, 1.0, 0.0, mv.run_speed, mv.accelerate, TICK)
    return math.hypot(vx, vy)


def jump(mv: Movement, start_speed: float, strafe_turn_deg_per_s: float = 0.0):
    """Jump from flat ground at start_speed along +x. Returns (air time, apex height, landing speed).

    With strafe_turn_deg_per_s the player air-strafes: wishdir stays perpendicular to the view, which
    turns at that rate (the classic strafe-jump input)."""
    vx, vy, vz, z, t = start_speed, 0.0, mv.jump_velocity, 0.0, 0.0
    apex, yaw = 0.0, 0.0
    while True:
        if strafe_turn_deg_per_s:
            yaw += math.radians(strafe_turn_deg_per_s) * TICK
            wx, wy = -math.sin(yaw), math.cos(yaw)
            vx, vy = accelerate(vx, vy, wx, wy, mv.run_speed, mv.air_accelerate, TICK, cap=mv.air_speed_cap)
        # Source applies half the gravity before and half after the move (exact for constant gravity).
        vz_new = vz - mv.gravity * TICK
        z += 0.5 * (vz + vz_new) * TICK
        vz = vz_new
        t += TICK
        apex = max(apex, z)
        if z <= 0:
            return t, apex, math.hypot(vx, vy)
