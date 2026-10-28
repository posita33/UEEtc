"""Run explicitly in a Cropout COPY with this plugin enabled. Creates NEW plugin assets only.

This does not implement the original BPI interfaces; wire the documented adapter nodes in the new BP.
"""
import json
import unreal

ROOT = "/CropoutVillager/Prototype"
TABLE = ROOT + "/DT_JobsNative"
BLUEPRINT = ROOT + "/BP_VillagerNative"
for path in (TABLE, BLUEPRINT):
    if unreal.EditorAssetLibrary.does_asset_exist(path):
        raise RuntimeError("Will not overwrite an existing asset: " + path)


def required(path):
    obj = unreal.load_asset(path)
    if obj is None:
        raise RuntimeError("Missing Cropout dependency: " + path)
    return obj


source = required("/Game/Blueprint/Villagers/DT_Jobs")
enum = required("/Game/Blueprint/Interactable/Extras/E_ResourceType")
body_mesh = required("/Game/Characters/Meshes/SKM_Villager")
body_anim = required("/Game/Characters/Animations/ABP_Villager").generated_class()
hair_anim = required("/Game/Characters/Animations/ABP_HAir").generated_class()
hat_anim = required("/Game/Characters/Meshes/Hats/ABP_Hat").generated_class()
crate = required("/Game/Characters/Meshes/Tools/Crate")
delivery = required("/Game/Characters/Animations/AM_PutDown")
shadow_material = required("/Game/Characters/Materials/MD_Shadow")
native_struct = unreal.load_object(None, "/Script/CropoutVillager.CropoutJobRow")
if native_struct is None:
    raise RuntimeError("Build and enable the CropoutVillager plugin first.")


def field(row, label):
    keys = [key for key in row if key == label or key.startswith(label + "_")]
    if len(keys) != 1:
        raise RuntimeError("Unexpected ST_Job field: " + label + " " + repr(keys))
    return row[keys[0]]


source_rows = json.loads(unreal.DataTableFunctionLibrary.export_data_table_to_json_string(source))
rows = [{"Name": row["Name"], "BehaviorTree": field(row, "BehaviourTree"),
         "WorkAnimation": field(row, "WorkAnim"), "Hat": field(row, "Hat"), "Tool": field(row, "Tool")}
        for row in source_rows]
tools = unreal.AssetToolsHelpers.get_asset_tools()
table_factory = unreal.DataTableFactory()
table_factory.set_editor_property("struct", native_struct)
table = tools.create_asset("DT_JobsNative", ROOT, unreal.DataTable, table_factory)
if table is None or not unreal.DataTableFunctionLibrary.fill_data_table_from_json_string(table, json.dumps(rows), native_struct):
    raise RuntimeError("Native table creation failed; inspect NEW plugin assets before retrying.")
bp_factory = unreal.BlueprintFactory()
bp_factory.set_editor_property("parent_class", unreal.CropoutVillagerPawn.static_class())
bp = tools.create_asset("BP_VillagerNative", ROOT, unreal.Blueprint, bp_factory)
if bp is None:
    raise RuntimeError("Native child Blueprint creation failed.")
unreal.BlueprintEditorLibrary.compile_blueprint(bp)
defaults = unreal.get_default_object(bp.generated_class())
defaults.set_editor_property("jobs", table)
defaults.set_editor_property("resource_crate", crate)
defaults.set_editor_property("delivery_montage", delivery)
if not defaults.configure_resource_enum(enum):
    raise RuntimeError("Cannot map the original enum identifiers.")
defaults.get_editor_property("body").set_skeletal_mesh_asset(body_mesh)
defaults.get_editor_property("body").set_anim_instance_class(body_anim)
defaults.get_editor_property("hair").set_anim_instance_class(hair_anim)
defaults.get_editor_property("hat").set_anim_instance_class(hat_anim)
defaults.get_editor_property("shadow").set_decal_material(shadow_material)
for asset in (table, bp):
    if not unreal.EditorAssetLibrary.save_loaded_asset(asset):
        raise RuntimeError("Cannot save NEW plugin asset: " + asset.get_path_name())
unreal.log("UEETC_NATIVE_VILLAGER_PREPARED: " + BLUEPRINT + " jobs=" + str(len(rows)))
