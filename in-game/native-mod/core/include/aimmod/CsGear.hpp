#pragma once
// CS weapons in the hand and the bomb in the world (in-game/docs/game-modes.md 6.6.2), the
// engine-free half: which slot to switch to, when the planted bomb's light flashes, and the
// shapes of AimMod's own knife and bomb models (engine basic shapes, tinted). AimModCore's
// CsGear applies them; only in AimMod match scenarios.
#include <aimmod/MatchPlay.hpp>

#include <array>
#include <string>
#include <vector>

namespace aimmod::cs
{
    // KovaaK's Weapon1..Weapon4 keys, numbered from 0.
    constexpr int PrimarySlot = 0, PistolSlot = 1, KnifeSlot = 2, BombSlot = 3, Slots = 4;
    // The knife's right-mouse stab, claimed as this slot (the host knows it as the stab).
    constexpr int StabSlot = 4;

    // The four CS slots' weapon profiles ("" or "-": empty).
    struct Loadout
    {
        std::array<std::string, Slots> names;
        bool Has(int slot) const { return slot >= 0 && slot < Slots && !names[static_cast<std::size_t>(slot)].empty() && names[static_cast<std::size_t>(slot)] != "-"; }
    };
    // From round-state.tsv's loadout line; the knife and bomb columns may be missing (empty).
    Loadout FromRound(const RoundState::Loadout& line);
    // The weapon CS would draw: primary, pistol, knife, bomb; -1 if nothing.
    int Best(const Loadout& l);
    // The mouse wheel: +1 the next slot (wheel down), -1 the previous (wheel up), skipping empty
    // slots and wrapping round; `current` when no other slot has a weapon.
    int Cycle(const Loadout& l, int current, int direction);

    // Which slot is in the hand and which was before it (Q: the last weapon).
    class Switcher
    {
    public:
        // The slot the game has in the hand now (after a key press of its own or ours).
        void Observe(int slot);
        int current() const { return m_current; }
        int last() const { return m_last; }
        // Q: the weapon before this one, if it is still there; else the best other one; -1 if none.
        int Previous(const Loadout& l) const;
        void Reset() { m_current = m_last = -1; }

    private:
        int m_current{-1}, m_last{-1};
    };

    // After the host's loadout changed: the slot to draw, or -1 to keep the weapon in hand.
    //  - something new in a slot (a purchase, the bomb picked up) is drawn, the primary first,
    //    except the bomb, which only fills its slot (CS keeps your gun in hand);
    //  - the slot in hand went empty (the bomb dropped or planted, a weapon lost): the best one;
    //  - the first loadout of a round: the best one.
    int AfterLoadout(const Loadout* before, const Loadout& after, int inHand);

    // The planted bomb's light, in step with the service's beep (BombSounds): beeps at 40 s left,
    // then every BeepInterval; the light flashes for 0.1 s at each, and stays on in the last second.
    double BeepInterval(double remainingSeconds);
    bool LightOn(double remainingSeconds);

    // AimMod's own simple models: parts of engine basic shapes (cube, cylinder, sphere; 100 cm),
    // in the model's own centimetres (x forward along its length, y right, z up), tinted.
    struct Part
    {
        const wchar_t* mesh;   // /Engine/BasicShapes/...
        double offset[3];      // part centre, cm
        double size[3];        // cm
        double rotation[3];    // pitch, yaw, roll (degrees)
        double colour[3];      // linear RGB
        bool light;            // the bomb's blinking light
    };
    // A C4-like charge: a dark olive block with a light keypad, a green display, two black
    // bands and a red light; about 26 x 16 x 8 cm, resting on its bottom face.
    const std::vector<Part>& BombModel();
    // A knife: a steel blade with a dark guard and handle, about 30 cm long, along x.
    const std::vector<Part>& KnifeModel();
    // Where the model sits in the first-person view, relative to the camera (cm, degrees).
    struct Hold
    {
        double offset[3], rotation[3];
    };
    Hold InHand(int slot);

    // The knife's attacks, drawn by moving AimMod's knife model (KovaaK's has no melee animation for
    // the first-person arms): slashes alternate right-to-left and left-to-right, about 0.25 s each with a
    // quick return; the right-mouse stab thrusts forward, about 0.38 s, at most once a second (CS2).
    enum class KnifeMove
    {
        None,
        SlashRight, // the blade sweeps from the right to the left
        SlashLeft,
        Stab,
    };
    constexpr double StabInterval = 1.0;
    double KnifeMoveSeconds(KnifeMove move);
    // The change from the rest pose `t` seconds into a move (zero before, after and for None).
    Hold KnifePose(KnifeMove move, double t);
    // The slash after `previous` (alternating; a stab or nothing starts with a right slash).
    KnifeMove NextSlash(KnifeMove previous);
} // namespace aimmod::cs
