// World tag checks (included by CoreTests.cpp): the service's roster and the JS payload.
#include <aimmod/WorldTags.hpp>

namespace worldtags_checks
{
    using namespace aimmod::worldtags;

    inline void Run()
    {
        auto roster = Parse("AIMMOD_TAGS_1\t7\ntag\ts-0011223344556677\tfriend\tT\t1\tNova%20Prime\ntag\ts-8899aabbccddeeff\tenemy\tCT\t0\tKestrel\n");
        CHECK(roster && roster->size() == 2 && roster->at("s-0011223344556677").friendly && roster->at("s-0011223344556677").name == "Nova Prime" &&
                  roster->at("s-0011223344556677").team == "T" && !roster->at("s-8899aabbccddeeff").alive,
              "world tags: friends and enemies by stream, names unescaped");
        CHECK(!Parse("") && !Parse("AIMMOD_TAGS_2\t1\n") && !Parse("AIMMOD_TAGS_1\t1\ntag\tbad id\tfriend\tT\t1\tx\n") &&
                  !Parse("AIMMOD_TAGS_1\t1\ntag\ts-1\tally\tT\t1\tx\n") && !Parse("AIMMOD_TAGS_1\t1\ntag\ts-1\tfriend\tX\t1\tx\n") &&
                  !Parse("AIMMOD_TAGS_1\t1\ntag\ts-1\tfriend\tT\t1\t%4\n"),
              "world tags: malformed rosters are ignored whole");
        const Who mate{true, "CT", true, "Mate"}, foe{false, "T", true, "Foe"}, deadFoe{false, "T", false, "Gone"};
        CHECK(Shown(mate, false, false) && !Shown(foe, false, true) && !Shown(foe, true, false) && Shown(foe, true, true) && !Shown(deadFoe, true, true),
              "world tags: teammates always (through walls); enemies only aimed at and in sight");
        const auto json = Json({ScreenTag{"Na\"me", "T", true, true, false, 0.5, 0.25, 12.4}});
        CHECK(json == R"({"tags":[{"n":"Na\"me","t":"T","f":1,"a":1,"c":0,"x":0.5000,"y":0.2500,"d":12}]})", "world tags: the JS payload");
        CHECK(Json({}) == R"({"tags":[]})", "world tags: none on screen clears them");
    }
} // namespace worldtags_checks
