#include "MaterialParams.hpp"

#include "Reflect.hpp"

#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>

#include <vector>

namespace aimmod
{
    using namespace game;

    bool MaterialParams::Bind()
    {
        m_materialInstance = FindClass(STR("/Script/Engine.MaterialInstance"));
        m_material = FindClass(STR("/Script/Engine.Material"));
        m_skinnedMesh = FindClass(STR("/Script/Engine.SkinnedMeshComponent"));
        m_miParent.Bind(m_materialInstance, "Parent");
        const char* arrays[3] = {"ScalarParameterValues", "VectorParameterValues", "TextureParameterValues"};
        for (int k = 0; k < 3; ++k)
        {
            m_miArrays[k].Bind(m_materialInstance, arrays[k]);
            m_miNames[k].Bind(m_miArrays[k].elementStruct(), "ParameterInfo.Name");
            m_materialEntries[k].Bind(m_material, "CachedExpressionData.Parameters.RuntimeEntries[" + std::to_string(k) + "].ParameterInfos");
        }
        m_infoName.Bind(m_materialEntries[1].elementStruct(), "Name");
        m_meshAsset.Bind(m_skinnedMesh, "SkeletalMesh");
        m_meshMaterials.Bind(FindClass(STR("/Script/Engine.SkeletalMesh")), "Materials");
        m_slotMaterial.Bind(m_meshMaterials.elementStruct(), "MaterialInterface");
        m_ok = m_miParent.ok() && m_miNames[0].ok() && m_miNames[1].ok() && m_materialInstance && m_material;
        return m_ok;
    }

    void MaterialParams::Names(UObject* material, std::set<std::string>& vectors, std::set<std::string>& scalars, std::set<std::string>& textures) const
    {
        std::set<std::string>* sets[3] = {&scalars, &vectors, &textures};
        UObject* current = material;
        for (int depth = 0; depth < 8 && m_ok && reflect::Alive(current); ++depth)
        {
            if (current->IsA(m_materialInstance))
            {
                for (int k = 0; k < 3; ++k)
                {
                    std::vector<const std::uint8_t*> entries;
                    if (!m_miNames[k].ok() || !m_miArrays[k].Elements(current, entries, 128)) continue;
                    for (const std::uint8_t* e : entries)
                        if (std::string n; m_miNames[k].Name(e, n) && !n.empty() && n != "None") sets[k]->insert(n);
                }
                current = m_miParent.Object(current);
                continue;
            }
            if (current->IsA(m_material) && baseReadable())
                for (int k = 0; k < 3; ++k)
                {
                    std::vector<const std::uint8_t*> infos;
                    if (!m_materialEntries[k].ok() || !m_materialEntries[k].Elements(current, infos, 256)) continue;
                    for (const std::uint8_t* e : infos)
                        if (std::string n; m_infoName.Name(e, n) && !n.empty() && n != "None") sets[k]->insert(n);
                }
            break;
        }
    }

    bool MaterialParams::Has(UObject* material, const std::set<std::string>& vectors, const std::set<std::string>& scalars) const
    {
        if (!reflect::Alive(material)) return false;
        std::set<std::string> v, s, t;
        Names(material, v, s, t);
        for (const std::string& n : vectors)
            if (!v.contains(n)) return false;
        for (const std::string& n : scalars)
            if (!s.contains(n)) return false;
        return true;
    }

    std::string MaterialParams::Describe(UObject* material) const
    {
        if (!reflect::Alive(material)) return "none";
        std::set<std::string> v, s, t;
        Names(material, v, s, t);
        std::string out = ObjectName(material) + " (vectors";
        for (const std::string& n : v) out += " " + n;
        out += "; scalars";
        for (const std::string& n : s) out += " " + n;
        return out + ")";
    }

    UObject* MaterialParams::MeshDefault(UObject* meshComponent, int slot) const
    {
        if (!reflect::Alive(meshComponent) || !m_skinnedMesh || !meshComponent->IsA(m_skinnedMesh) || !m_meshAsset.ok() || !m_meshMaterials.ok() || !m_slotMaterial.ok()) return nullptr;
        UObject* mesh = m_meshAsset.Object(meshComponent);
        std::vector<const std::uint8_t*> slots;
        if (!reflect::Alive(mesh) || !m_meshMaterials.Elements(mesh, slots, 32) || slot < 0 || slot >= static_cast<int>(slots.size())) return nullptr;
        UObject* material = m_slotMaterial.Object(slots[static_cast<std::size_t>(slot)]);
        return reflect::Alive(material) ? material : nullptr;
    }
} // namespace aimmod
