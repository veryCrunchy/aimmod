namespace kmod_replay {

struct ReplayRawVec3 {
    float x{0.0f};
    float y{0.0f};
    float z{0.0f};
};

struct ReplayRawRotator {
    float pitch{0.0f};
    float yaw{0.0f};
    float roll{0.0f};
};

static auto replay_normalize_ascii(const RC::StringType& input) -> std::string {
    std::string out;
    out.reserve(input.size());
    for (auto c : input) {
        const auto ch = static_cast<unsigned int>(c);
        if (ch > 0x7F) {
            continue;
        }
        out.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
    }
    return out;
}

static auto replay_object_path_from_full_name(const RC::StringType& full_name) -> RC::StringType {
    const auto split = full_name.find(STR(" "));
    if (split == RC::StringType::npos) {
        return full_name;
    }
    if (split + 1 >= full_name.size()) {
        return RC::StringType{};
    }
    return full_name.substr(split + 1);
}

static auto replay_is_rejected_runtime_object_name(const RC::StringType& full_name) -> bool {
    if (full_name.empty()) {
        return true;
    }
    return full_name.find(STR("Default__")) != RC::StringType::npos
        || full_name.find(STR("/Script/")) != RC::StringType::npos
        || full_name.find(STR("Class ")) != RC::StringType::npos
        || full_name.find(STR("Function ")) != RC::StringType::npos;
}

static auto replay_enumerate_properties(RC::Unreal::UStruct* owner) -> std::vector<RC::Unreal::FProperty*> {
    std::vector<RC::Unreal::FProperty*> out{};
    if (!owner || !is_likely_valid_object_ptr(owner)) {
        return out;
    }
    std::unordered_set<RC::Unreal::FProperty*> seen{};
    auto* property = owner->GetPropertyLink();
    while (property && is_likely_valid_object_ptr(property)) {
        if (!seen.insert(property).second) {
            break;
        }
        out.emplace_back(property);
        auto* next = property->GetPropertyLinkNext();
        if (next == property) {
            break;
        }
        property = next;
    }
    return out;
}

static auto replay_enumerate_properties_in_chain(RC::Unreal::UStruct* owner) -> std::vector<RC::Unreal::FProperty*> {
    std::vector<RC::Unreal::FProperty*> out{};
    if (!owner || !is_likely_valid_object_ptr(owner)) {
        return out;
    }

    std::unordered_set<RC::Unreal::UStruct*> seen_structs{};
    std::unordered_set<RC::Unreal::FProperty*> seen_props{};
    for (auto* current = owner; current && is_likely_valid_object_ptr(current); current = current->GetSuperStruct()) {
        if (!seen_structs.insert(current).second) {
            break;
        }
        for (auto* property : replay_enumerate_properties(current)) {
            if (!property || !is_likely_valid_object_ptr(property)) {
                continue;
            }
            if (seen_props.insert(property).second) {
                out.emplace_back(property);
            }
        }
    }
    return out;
}

static auto replay_read_fstring(void* value_ptr, std::string& out_utf8) -> bool {
    out_utf8.clear();
    if (!value_ptr) {
        return false;
    }

    struct RawFStringData {
        const RC::Unreal::TCHAR* data;
        int32_t count;
        int32_t capacity;
    };

    const auto* raw = reinterpret_cast<const RawFStringData*>(value_ptr);
    if (!raw || !is_likely_readable_region(raw, sizeof(RawFStringData))) {
        return false;
    }
    if (!raw->data || raw->count <= 1 || raw->count > 4096) {
        return false;
    }
    if (!is_likely_readable_region(raw->data, static_cast<size_t>(raw->count) * sizeof(RC::Unreal::TCHAR))) {
        return false;
    }

    RC::StringType wide(raw->data, raw->data + static_cast<size_t>(raw->count - 1));
    if (wide.empty()) {
        return false;
    }
    out_utf8 = utf8_from_wide(wide);
    return !out_utf8.empty();
}

static auto replay_read_string_property(
    RC::Unreal::UObject* owner,
    const char* wanted_name,
    std::string& out_value
) -> bool {
    out_value.clear();
    if (!owner || !wanted_name || !*wanted_name) {
        return false;
    }
    auto* owner_class = owner->GetClassPrivate();
    if (!owner_class || !is_likely_valid_object_ptr(owner_class)) {
        return false;
    }

    for (auto* property : replay_enumerate_properties_in_chain(owner_class)) {
        if (!property || !is_likely_valid_object_ptr(property)) {
            continue;
        }
        if (replay_normalize_ascii(property->GetName()) != wanted_name) {
            continue;
        }

        auto* str_property = RC::Unreal::CastField<RC::Unreal::FStrProperty>(property);
        if (!str_property || !is_likely_valid_object_ptr(str_property)) {
            continue;
        }

        void* value_ptr = safe_property_value_ptr(str_property, owner);
        if (!value_ptr) {
            continue;
        }

        if (replay_read_fstring(value_ptr, out_value)) {
            return true;
        }
    }
    return false;
}

static auto replay_read_object_presence_property(
    RC::Unreal::UObject* owner,
    const char* wanted_name,
    bool& out_present
) -> bool {
    out_present = false;
    if (!owner || !wanted_name || !*wanted_name) {
        return false;
    }
    auto* owner_class = owner->GetClassPrivate();
    if (!owner_class || !is_likely_valid_object_ptr(owner_class)) {
        return false;
    }

    for (auto* property : replay_enumerate_properties_in_chain(owner_class)) {
        if (!property || !is_likely_valid_object_ptr(property)) {
            continue;
        }
        if (replay_normalize_ascii(property->GetName()) != wanted_name) {
            continue;
        }

        auto* object_property = RC::Unreal::CastField<RC::Unreal::FObjectPropertyBase>(property);
        if (!object_property || !is_likely_valid_object_ptr(object_property)) {
            continue;
        }

        void* value_ptr = safe_property_value_ptr(object_property, owner);
        if (!value_ptr || !is_likely_readable_region(value_ptr, sizeof(void*))) {
            continue;
        }

        auto* referenced = object_property->GetObjectPropertyValue(value_ptr);
        out_present = (referenced != nullptr && is_likely_valid_object_ptr(referenced));
        return true;
    }

    return false;
}

static auto replay_resolve_actor_fn(const wchar_t* path) -> RC::Unreal::UFunction* {
    auto* fn = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UFunction*>(
        nullptr,
        nullptr,
        path
    );
    if (!fn || !is_likely_valid_object_ptr(fn)) {
        return nullptr;
    }
    return fn;
}

static auto replay_read_actor_transforms(
    RC::Unreal::UObject* actor,
    ReplayEntity& out_entity
) -> bool {
    if (!actor || !is_likely_valid_object_ptr(actor)) {
        return false;
    }

    static RC::Unreal::UFunction* fn_get_location = nullptr;
    static RC::Unreal::UFunction* fn_get_rotation = nullptr;
    static RC::Unreal::UFunction* fn_get_velocity = nullptr;

    if (!fn_get_location) {
        fn_get_location = replay_resolve_actor_fn(STR("/Script/Engine.Actor:K2_GetActorLocation"));
    }
    if (!fn_get_rotation) {
        fn_get_rotation = replay_resolve_actor_fn(STR("/Script/Engine.Actor:K2_GetActorRotation"));
    }
    if (!fn_get_velocity) {
        fn_get_velocity = replay_resolve_actor_fn(STR("/Script/Engine.Actor:GetVelocity"));
    }

    bool has_any = false;

    if (fn_get_location) {
        struct Params {
            ReplayRawVec3 ReturnValue{};
        } params{};
        actor->ProcessEvent(fn_get_location, &params);
        if (std::isfinite(params.ReturnValue.x)
            && std::isfinite(params.ReturnValue.y)
            && std::isfinite(params.ReturnValue.z)) {
            out_entity.location.x = params.ReturnValue.x;
            out_entity.location.y = params.ReturnValue.y;
            out_entity.location.z = params.ReturnValue.z;
            has_any = true;
        }
    }

    if (fn_get_rotation) {
        struct Params {
            ReplayRawRotator ReturnValue{};
        } params{};
        actor->ProcessEvent(fn_get_rotation, &params);
        if (std::isfinite(params.ReturnValue.pitch)
            && std::isfinite(params.ReturnValue.yaw)
            && std::isfinite(params.ReturnValue.roll)) {
            out_entity.rotation.pitch = params.ReturnValue.pitch;
            out_entity.rotation.yaw = params.ReturnValue.yaw;
            out_entity.rotation.roll = params.ReturnValue.roll;
            has_any = true;
        }
    }

    if (fn_get_velocity) {
        struct Params {
            ReplayRawVec3 ReturnValue{};
        } params{};
        actor->ProcessEvent(fn_get_velocity, &params);
        if (std::isfinite(params.ReturnValue.x)
            && std::isfinite(params.ReturnValue.y)
            && std::isfinite(params.ReturnValue.z)) {
            out_entity.velocity.x = params.ReturnValue.x;
            out_entity.velocity.y = params.ReturnValue.y;
            out_entity.velocity.z = params.ReturnValue.z;
            has_any = true;
        }
    }

    return has_any;
}

// FindAllOf walks every UObject. Replay samples run at up to 60Hz, so the
// object lists are cached and refreshed on a slower cadence; each sample still
// reads live values from the cached objects (validated before use).
struct ReplayFindAllCache {
    std::vector<RC::Unreal::UObject*> objects{};
    uint64_t refreshed_ms = 0;
};

static auto replay_find_all_cached(
    ReplayFindAllCache& cache,
    const wchar_t* primary_class,
    const wchar_t* secondary_class,
    uint64_t max_age_ms
) -> const std::vector<RC::Unreal::UObject*>& {
    const uint64_t now = GetTickCount64();
    if (cache.refreshed_ms != 0 && now - cache.refreshed_ms < max_age_ms) {
        return cache.objects;
    }
    cache.objects.clear();
    RC::Unreal::UObjectGlobals::FindAllOf(primary_class, cache.objects);
    std::vector<RC::Unreal::UObject*> alt{};
    RC::Unreal::UObjectGlobals::FindAllOf(secondary_class, alt);
    for (auto* obj : alt) {
        if (obj) {
            cache.objects.emplace_back(obj);
        }
    }
    cache.refreshed_ms = now;
    return cache.objects;
}

static auto replay_collect_map_context(ReplayContext& context) -> void {
    // The game state only changes on map load.
    static ReplayFindAllCache game_state_cache{};
    const std::vector<RC::Unreal::UObject*> candidates =
        replay_find_all_cached(game_state_cache, STR("MetaGameState"), STR("KovGameState_C"), 2000);

    // Map name and texture scale only change with the game state, so the
    // result is reused until the game-state list refreshes (every 2s).
    struct CachedMapContext {
        uint64_t generation = 0;
        bool has_map_name = false;
        std::string map_name{};
        bool has_map_scale = false;
        float map_scale = 0.0f;
    };
    static CachedMapContext cached_map{};
    if (cached_map.generation != 0 && cached_map.generation == game_state_cache.refreshed_ms) {
        if (cached_map.has_map_name) {
            context.map_name = cached_map.map_name;
        }
        if (cached_map.has_map_scale) {
            context.map_scale = cached_map.map_scale;
        }
        return;
    }
    cached_map = CachedMapContext{};
    cached_map.generation = game_state_cache.refreshed_ms;

    RC::Unreal::UObject* best = nullptr;
    int best_score = std::numeric_limits<int>::min();

    for (auto* obj : candidates) {
        if (!obj || !is_likely_valid_object_ptr(obj)) {
            continue;
        }
        const auto full_name = obj->GetFullName();
        if (replay_is_rejected_runtime_object_name(full_name)) {
            continue;
        }

        int score = 0;
        if (full_name.find(STR("/Engine/Transient.")) != RC::StringType::npos) score += 80;
        if (full_name.find(STR("KovGameState_C_")) != RC::StringType::npos) score += 120;
        if (full_name.find(STR("Default__")) != RC::StringType::npos) score -= 1000;
        if (full_name.find(STR("/Script/")) != RC::StringType::npos) score -= 200;

        if (!best || score > best_score) {
            best = obj;
            best_score = score;
        }
    }

    if (!best) {
        return;
    }

    std::string map_name{};
    if (replay_read_string_property(best, "currentmapname", map_name)) {
        context.map_name = map_name;
        cached_map.has_map_name = true;
        cached_map.map_name = map_name;
    }

    auto* owner_class = best->GetClassPrivate();
    if (!owner_class || !is_likely_valid_object_ptr(owner_class)) {
        return;
    }
    for (auto* property : replay_enumerate_properties_in_chain(owner_class)) {
        if (!property || !is_likely_valid_object_ptr(property)) {
            continue;
        }
        if (replay_normalize_ascii(property->GetName()) != "texturescale") {
            continue;
        }
        auto* numeric = RC::Unreal::CastField<RC::Unreal::FNumericProperty>(property);
        if (!numeric || !is_likely_valid_object_ptr(numeric)) {
            continue;
        }
        void* value_ptr = safe_property_value_ptr(numeric, best);
        if (!value_ptr) {
            continue;
        }
        float value = 0.0f;
        if (numeric->IsFloatingPoint()) {
            value = static_cast<float>(numeric->GetFloatingPointPropertyValue(value_ptr));
        } else if (numeric->IsInteger()) {
            value = static_cast<float>(numeric->GetSignedIntPropertyValue(value_ptr));
        } else {
            continue;
        }
        if (std::isfinite(value) && value >= 0.0f) {
            context.map_scale = value;
            cached_map.has_map_scale = true;
            cached_map.map_scale = value;
        }
        break;
    }
}

static auto replay_collect_entity_actor_refs(std::vector<ReplayEntityActorRef>& out_refs) -> void {
    out_refs.clear();

    // Bots spawn and despawn during a run; 250ms keeps new ones prompt.
    static ReplayFindAllCache character_cache{};
    const std::vector<RC::Unreal::UObject*> candidates =
        replay_find_all_cached(character_cache, STR("MetaCharacter"), STR("FPSCharacter_C"), 250);

    // Name and profile never change for an actor, so they are cached per actor;
    // transforms are still read live every sample. Controller presence can
    // arrive after the actor is first seen (spawned before possession), so an
    // actor with neither controller is read again once per list refresh.
    //
    // The list is reused for up to 250ms, and GC can free an actor in that
    // time. A freed actor's memory stays readable, so each cached actor is
    // looked up again by path before use: a freed one is not found, and an
    // address reused by a new actor resolves to a different object.
    struct CachedEntityInfo {
        bool usable = false;
        // Set when the actor failed the liveness check; dropped on refresh.
        bool gone = false;
        RC::StringType path{};
        std::string id{};
        std::string profile{};
        bool is_player = false;
        bool is_bot = false;
        // List generation the controller fields were last read in.
        uint64_t controllers_read_generation = 0;
    };
    static std::unordered_map<RC::Unreal::UObject*, CachedEntityInfo> entity_info_cache{};
    static uint64_t entity_info_generation = 0;
    if (entity_info_generation != character_cache.refreshed_ms) {
        // Keep identities for actors still present; only new actors pay the
        // name/property lookups. Rebuilding every entry on each refresh made
        // replay sampling stall the mod for seconds with many characters.
        std::unordered_set<RC::Unreal::UObject*> present(candidates.begin(), candidates.end());
        for (auto it = entity_info_cache.begin(); it != entity_info_cache.end();) {
            if (present.count(it->first) == 0 || it->second.gone) {
                it = entity_info_cache.erase(it);
            } else {
                ++it;
            }
        }
        entity_info_generation = character_cache.refreshed_ms;
    }

    std::unordered_set<std::string> seen_ids{};

    for (auto* obj : candidates) {
        if (!obj || !is_likely_valid_object_ptr(obj)) {
            continue;
        }

        auto cached = entity_info_cache.find(obj);
        if (cached != entity_info_cache.end()) {
            CachedEntityInfo& known = cached->second;
            if (known.gone) {
                continue;
            }
            if (known.usable) {
                auto* live = RC::Unreal::UObjectGlobals::StaticFindObject<RC::Unreal::UObject*>(
                    nullptr,
                    nullptr,
                    known.path.c_str()
                );
                if (live != obj) {
                    // Freed, or the address now holds another actor. Skip it
                    // until the next refresh; touching it could crash.
                    known.gone = true;
                    continue;
                }
            }
        }
        if (cached == entity_info_cache.end()) {
            CachedEntityInfo info{};
            const auto full_name = obj->GetFullName();
            if (!replay_is_rejected_runtime_object_name(full_name)) {
                info.path = replay_object_path_from_full_name(full_name);
                info.id = utf8_from_wide(info.path);
                info.usable = !info.id.empty();
            }
            if (info.usable) {
                std::string profile{};
                if (replay_read_string_property(obj, "characterprofilename", profile)) {
                    info.profile = profile;
                }
            }
            cached = entity_info_cache.emplace(obj, std::move(info)).first;
        }
        CachedEntityInfo& info = cached->second;
        if (!info.usable) {
            continue;
        }
        if (!info.is_player && !info.is_bot
            && info.controllers_read_generation != entity_info_generation) {
            bool has_player = false;
            bool has_ai = false;
            info.is_player =
                replay_read_object_presence_property(obj, "myplayercontroller", has_player) && has_player;
            info.is_bot = replay_read_object_presence_property(obj, "myaicontroller", has_ai) && has_ai;
            info.controllers_read_generation = entity_info_generation;
        }
        if (!seen_ids.insert(info.id).second) {
            continue;
        }

        ReplayEntity entity{};
        entity.id = info.id;
        entity.profile = info.profile;
        entity.is_player = info.is_player;
        entity.is_bot = info.is_bot;

        (void)replay_read_actor_transforms(obj, entity);

        ReplayEntityActorRef ref{};
        ref.entity = std::move(entity);
        ref.actor = obj;
        out_refs.emplace_back(std::move(ref));
    }
}

static auto replay_collect_entities(std::vector<ReplayEntity>& out_entities) -> void {
    out_entities.clear();

    std::vector<ReplayEntityActorRef> refs{};
    replay_collect_entity_actor_refs(refs);
    out_entities.reserve(refs.size());
    for (auto& ref : refs) {
        out_entities.emplace_back(std::move(ref.entity));
    }
}

} // namespace kmod_replay
